using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartUnzip.Core;

/// <summary>
/// 基于 7-Zip 命令行的解压引擎。
///
/// 关键设计：
///  - 路径绝不写死：按 配置 → 已装 7-Zip → 随附 7z.exe → PATH → 注册表 的顺序探测。
///  - 用进程调用而非引用 7z.dll，换取可靠的进度解析与进程级取消。
///  - 密码只通过命令行参数传递，永不写入日志。
/// </summary>
public sealed partial class SevenZipEngine : IArchiveEngine
{
    private readonly Logger _log;

    public string Name => "7-Zip";
    public bool IsAvailable => !string.IsNullOrEmpty(EnginePath) && File.Exists(EnginePath);
    public string? EnginePath { get; private set; }
    public string? Version { get; private set; }

    public SevenZipEngine(Logger log)
    {
        _log = log;
        Detect();
    }

    /// <summary>
    /// 自动探测 7-Zip。顺序：
    ///  1. 环境变量 SMARTUNZIP_7Z
    ///  2. 随附在程序目录的 7z.exe / 7za.exe / 7zz.exe
    ///  3. 常见安装路径（Program Files / Program Files (x86) / %LOCALAPPDATA%\Programs\7-Zip）
    ///  4. PATH
    ///  5. 注册表 7-Zip 安装项
    /// </summary>
    public void Detect()
    {
        EnginePath = null;
        Version = null;

        foreach (var candidate in EnumerateCandidates())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
                var ver = QueryVersion(candidate);
                if (ver is null) continue;
                EnginePath = candidate;
                Version = ver;
                _log.Info($"检测到 7-Zip 引擎：{candidate}（版本 {ver}）");
                return;
            }
            catch
            {
                // 尝试下一个候选
            }
        }

        _log.Warn("未检测到可用的 7-Zip 引擎。");
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        foreach (var c in EnumerateCandidatesCore())
            yield return c;
    }

    private static IEnumerable<string> EnumerateCandidatesCore()
    {
        // 1. 环境变量
        var env = Environment.GetEnvironmentVariable("SMARTUNZIP_7Z");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;

        // 2. 随附程序目录
        var baseDir = AppContext.BaseDirectory;
        foreach (var name in new[] { "7z.exe", "7za.exe", "7zz.exe" })
        {
            yield return Path.Combine(baseDir, name);
            yield return Path.Combine(baseDir, "7zip", name);
            yield return Path.Combine(baseDir, "engine", name);
        }

        // 3. 常见安装路径
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var dirs = new[]
        {
            Path.Combine(pf, "7-Zip"),
            Path.Combine(pf86, "7-Zip"),
            Path.Combine(localApp, "Programs", "7-Zip"),
            Path.Combine(localApp, "7-Zip"),
            @"C:\7-Zip",
            @"C:\Program Files\7-Zip",
            @"C:\Program Files (x86)\7-Zip",
        };
        foreach (var d in dirs)
        {
            yield return Path.Combine(d, "7z.exe");
            yield return Path.Combine(d, "7za.exe");
            yield return Path.Combine(d, "7zz.exe");
        }

        // 4. PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var p in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var d = p.Trim();
            if (d.Length == 0) continue;
            yield return Path.Combine(d, "7z.exe");
            yield return Path.Combine(d, "7za.exe");
            yield return Path.Combine(d, "7zz.exe");
        }

        // 5. 注册表（读取逻辑已隔离，避免在迭代器中 try/catch）
        foreach (var d in ReadRegistryDirs())
        {
            yield return Path.Combine(d, "7z.exe");
            yield return Path.Combine(d, "7za.exe");
        }
    }

    /// <summary>从注册表读取 7-Zip 安装目录（HKLM / HKCU）。</summary>
    private static IReadOnlyList<string> ReadRegistryDirs()
    {
        var result = new List<string>();
        foreach (var sub in new[] { @"SOFTWARE\7-Zip", @"SOFTWARE\WOW6432Node\7-Zip" })
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    using var k = hive.OpenSubKey(sub);
                    var v = k?.GetValue("Path") as string;
                    if (!string.IsNullOrWhiteSpace(v) && !result.Contains(v, StringComparer.OrdinalIgnoreCase))
                        result.Add(v);
                }
                catch { }
            }
        }
        return result;
    }

    /// <summary>查询 7-Zip 版本。返回 null 表示不是可用的 7-Zip。</summary>
    private static string? QueryVersion(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            // 形如 "7-Zip 26.03 (x64) : Copyright ..."
            var m = VersionRegex().Match(output);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"7-Zip\s+([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    // ------------------------------------------------------------------
    // 核心操作
    // ------------------------------------------------------------------

    /// <summary>列出压缩包条目（解析 7z l -slt 输出）。</summary>
    public async Task<IReadOnlyList<string>> ListEntriesAsync(string archivePath, string? password,
        CancellationToken ct = default)
    {
        // 开关在前，"--" 之后只放文件名。
        // 始终显式给出 -p（空密码 = 明确告知"不提示密码"），避免 7-Zip 在无控制台的
        // GUI 进程中尝试打开 CONIN$ 交互读取密码而永久阻塞。
        var args = new List<string> { "l", "-slt", "-ba" };
        args.Add(string.IsNullOrEmpty(password) ? "-p" : $"-p{password}");
        args.Add("--");
        args.Add(archivePath);

        var (exit, stdout, _) = await RunAsync(args, null, ct).ConfigureAwait(false);
        var entries = new List<string>();
        if (exit != 0) return entries;

        foreach (var line in stdout.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.StartsWith("Path = ", StringComparison.Ordinal))
            {
                var p = t["Path = ".Length..];
                if (!string.Equals(p, archivePath, StringComparison.OrdinalIgnoreCase))
                    entries.Add(p);
            }
        }
        return entries;
    }

    /// <summary>判断是否加密（解析 l -slt 的 Encrypted 字段）。</summary>
    public async Task<bool> IsEncryptedAsync(string archivePath, CancellationToken ct = default)
    {
        var args = new List<string> { "l", "-slt", "-ba", "-p", "--", archivePath };
        var (exit, stdout, _) = await RunAsync(args, null, ct).ConfigureAwait(false);
        if (exit != 0) return false;

        // 未提供密码时，加密条目会显示 "Encrypted = +"，且可能报错。
        return stdout.Contains("Encrypted = +", StringComparison.Ordinal);
    }

    /// <summary>用 7z t 测试压缩包（验证密码）。</summary>
    public async Task<(bool ok, bool wrongPassword)> TestAsync(string archivePath, string? password,
        CancellationToken ct = default)
    {
        var args = new List<string> { "t" };
        if (!string.IsNullOrEmpty(password)) args.Add($"-p{password}");
        else args.Add("-p");
        args.Add("-y");
        args.Add("--");
        args.Add(archivePath);

        var (exit, stdout, stderr) = await RunAsync(args, null, ct).ConfigureAwait(false);
        var combined = stdout + "\n" + stderr;

        // 退出码 0 = 通过；2 = 致命错误（含 CRC/密码错误）；7 = 命令行错误；8 = 内存不足；255 = 用户中止
        if (exit == 0) return (true, false);

        var wrongPwd = combined.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Can not open encrypted archive. Wrong password", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Data Error in encrypted file. Wrong password", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("密码错误", StringComparison.Ordinal)
            || combined.Contains("CRC Failed in encrypted file", StringComparison.OrdinalIgnoreCase);

        return (false, wrongPwd);
    }

    /// <summary>解压。解析 -bsp1 的进度输出。</summary>
    public async Task<ExtractResult> ExtractAsync(ExtractRequest request, IProgress<ExtractProgress>? progress,
        CancellationToken ct = default)
    {
        var outDir = request.OutputDirectory;
        Directory.CreateDirectory(outDir);

        // 注意：7-Zip 的 "--" 表示"其后全部是文件名"。
        // 因此所有开关必须放在 "--" 之前，源文件路径放在最后。
        var args = new List<string>
        {
            "x",
            $"-o{outDir}",
            "-y",
            "-bsp1",   // 进度输出到 stdout
            "-bso0",   // 抑制普通信息
            "-bse1",   // 错误输出到 stderr
        };
        if (request.Overwrite) args.Add("-aoa"); else args.Add("-aos");
        if (!string.IsNullOrEmpty(request.Password)) args.Add($"-p{request.Password}");
        else args.Add("-p");

        args.Add("--");                    // -- 之后全部视为文件名
        args.Add(request.SourcePath);

        long total = 0;
        try
        {
            var fi = new FileInfo(request.SourcePath);
            if (fi.Exists) total = fi.Length;
        }
        catch { }

        var (exit, stdout, stderr) = await RunAsync(args, line =>
        {
            var parsed = ParseProgress(line, total);
            if (parsed is not null) progress?.Report(parsed);
        }, ct).ConfigureAwait(false);

        var combined = stdout + "\n" + stderr;

        if (exit == 0)
        {
            int count = 0;
            try
            {
                count = Directory.EnumerateFileSystemEntries(outDir, "*", SearchOption.AllDirectories).Count();
            }
            catch { }
            return new ExtractResult
            {
                Success = true,
                ExitCode = exit,
                OutputDirectory = outDir,
                FileCount = count
            };
        }

        var wrong = combined.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Can not open encrypted archive", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("CRC Failed in encrypted file", StringComparison.OrdinalIgnoreCase);

        var msg = MapError(exit, combined);

        // 用户取消 → 255
        if (ct.IsCancellationRequested || exit == 255)
        {
            return new ExtractResult
            {
                Success = false,
                ExitCode = exit,
                Cancelled = true,
                ErrorMessage = "已取消",
                OutputDirectory = outDir
            };
        }

        return new ExtractResult
        {
            Success = false,
            ExitCode = exit,
            WrongPassword = wrong,
            ErrorMessage = msg,
            OutputDirectory = outDir
        };
    }

    /// <summary>解析 7-Zip 的进度行，形如 " 45% 12 - filename"。</summary>
    private static ExtractProgress? ParseProgress(string line, long totalBytes)
    {
        var m = ProgressRegex().Match(line);
        if (!m.Success) return null;

        if (!double.TryParse(m.Groups[1].Value, out var pct)) return null;
        var processed = totalBytes > 0 ? (long)(totalBytes * pct / 100.0) : 0;

        return new ExtractProgress
        {
            Percent = Math.Clamp(pct, 0, 100),
            ProcessedBytes = processed,
            TotalBytes = totalBytes
        };
    }

    [GeneratedRegex(@"^\s*(\d{1,3})%")]
    private static partial Regex ProgressRegex();

    private static string MapError(int exitCode, string output)
    {
        if (output.Contains("not enough space", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("There is not enough space", StringComparison.OrdinalIgnoreCase))
            return "磁盘空间不足，请清理后重试。";

        if (output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("拒绝访问", StringComparison.Ordinal))
            return "权限不足，无法写入目标目录。";

        if (output.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("正被另一进程使用", StringComparison.Ordinal))
            return "文件被其他程序占用，请关闭后重试。";

        if (output.Contains("Cannot open", StringComparison.OrdinalIgnoreCase) && exitCode == 2)
            return "无法打开文件，可能已损坏或格式不受支持。";

        if (output.Contains("Unsupported Method", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("不支持的", StringComparison.Ordinal))
            return "该压缩包使用了当前 7-Zip 不支持的方法（如新版 RAR5 特殊算法）。";

        if (output.Contains("Path too long", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("文件名或扩展名太长", StringComparison.Ordinal))
            return "路径过长，请尝试解压到更短的目录。";

        return exitCode switch
        {
            1 => "解压完成，但有警告（部分文件可能未完全解压）。",
            2 => "解压失败：文件损坏或数据错误。",
            7 => "7-Zip 命令行参数错误。",
            8 => "内存不足。",
            255 => "操作已被取消。",
            _ => $"解压失败（7-Zip 退出码 {exitCode}）。"
        };
    }

    private static readonly SemaphoreSlim ProcessGate = new(4, 4);

    /// <summary>
    /// 运行 7-Zip 进程，实时回调 stdout 行。返回 (exitCode, stdout, stderr)。
    ///
    /// 实现要点（踩坑记录）：
    ///   不要混用 <c>BeginOutputReadLine</c> 与 <c>WaitForExitAsync</c>。
    ///   在 WinExe（无控制台）进程中，当同一进程内第二次发起调用时，
    ///   <c>WaitForExitAsync</c> 可能永远等待 stdout/stderr 管道达到 EOF 而不返回，
    ///   即使 7z.exe 本体早已退出。改为在专用线程上同步读取流，
    ///   让读取循环的自然结束来标志"输出读完"，全部用 Task.Run 包裹。
    /// </summary>
    private async Task<(int exit, string stdout, string stderr)> RunAsync(
        IReadOnlyList<string> args, Action<string>? onStdoutLine, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("未检测到 7-Zip 引擎，无法执行解压。");

        await ProcessGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = EnginePath!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 关键：GUI 进程（WinExe）没有控制台。若不重定向 stdin，7-Zip 在需要
                // 交互输入（例如密码提示）时会尝试打开 CONIN$ 并永久阻塞，导致解压流程挂死。
                // 这里重定向后立即关闭，使子进程读到 EOF 而不会等待。
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            _log.Debug($"执行 7-Zip：{SanitizeArgs(args)}");

            // 整个过程（启动 + 读取 + 等待退出）放在独立后台线程上执行，
            // 避免 UI 同步上下文介入，保证进度回调与取消都稳定可控。
            var tcs = new TaskCompletionSource<(int exit, string stdout, string stderr)>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var worker = new Thread(() =>
            {
                try
                {
                    var r = RunCore(psi, args, onStdoutLine, ct);
                    tcs.TrySetResult(r);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            })
            { IsBackground = true, Name = "Flux-7z" };
            worker.Start();

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    /// <summary>在专用线程上执行一次 7-Zip 调用（同步阻塞实现，见 RunAsync 注释）。</summary>
    private (int exit, string stdout, string stderr) RunCore(
        ProcessStartInfo psi, IReadOnlyList<string> args, Action<string>? onStdoutLine, CancellationToken ct)
    {
        using var p = new Process { StartInfo = psi };
        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();

        if (!p.Start())
            throw new InvalidOperationException("无法启动 7-Zip 进程。");

        // stdin 立即关闭：任何读取请求立刻得到 EOF，避免 7-Zip 在无控制台的 GUI 进程中
        // 等待交互输入而挂起。
        try { p.StandardInput.Close(); } catch { }

        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { }
        });

        // 同步逐块读取 stdout / stderr（各自线程），读取循环结束 = 管道 EOF。
        var errText = new StringBuilder();
        var errThread = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = p.StandardError.ReadLine()) is not null)
                    errText.AppendLine(line);
            }
            catch { }
        })
        { IsBackground = true };
        errThread.Start();

        // stdout 读取放到独立线程，主流程只负责等待进程退出。
        // 这样即使管道句柄因故未及时达到 EOF，也不会让整个流程挂死：
        // 进程退出后由主流程负责打断读线程。
        var outDone = new TaskCompletionSource();
        var outThread = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = p.StandardOutput.ReadLine()) is not null)
                {
                    sbOut.AppendLine(line);
                    try { onStdoutLine?.Invoke(line); } catch { }
                }
            }
            catch { }
            outDone.TrySetResult();
        })
        { IsBackground = true };
        outThread.Start();

        // 等待进程退出（轮询判断 + 硬超时 10 分钟保护）。
        var hardDeadline = DateTime.UtcNow.AddMinutes(10);
        while (IsProcessAlive(p.Id))
        {
            if (ct.IsCancellationRequested)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                break;
            }
            if (DateTime.UtcNow > hardDeadline)
            {
                _log.Warn("7-Zip 执行超时（10 分钟），已强制终止。");
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                break;
            }
            Thread.Sleep(50);
        }
        // 短暂等待进程句柄完成回收，以便读取 ExitCode
        for (int i = 0; i < 20 && !p.HasExited; i++) Thread.Sleep(25);

        // 给读线程一点时间收尾；若仍未结束，主动打断读取（管道可能残留句柄未关闭）。
        if (!outDone.Task.Wait(2000))
        {
            try { p.CancelOutputRead(); } catch { }
            try { p.StandardOutput.BaseStream.Dispose(); } catch { }
            outDone.Task.Wait(2000);
        }
        errThread.Join(2000);

        sbErr.Append(errText);
        return (p.ExitCode, sbOut.ToString(), sbErr.ToString());
    }

    /// <summary>判断指定 PID 的进程是否仍存活（不依赖 Process.WaitForExit）。</summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            // 进程不存在 → GetProcessById 抛 ArgumentException
            return false;
        }
    }

    /// <summary>日志输出前过滤密码参数。</summary>
    private static string SanitizeArgs(IReadOnlyList<string> args)
    {
        var list = args.Select(a =>
            a.StartsWith("-p", StringComparison.Ordinal) && a.Length > 2 ? "-p***" : a);
        return string.Join(' ', list);
    }
}
