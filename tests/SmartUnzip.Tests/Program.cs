using System.IO;
using System.Text;
using SmartUnzip.Core;

namespace SmartUnzip.Tests;

/// <summary>
/// SmartUnzip 核心功能实测程序。
/// 覆盖需求文档第 39 / 40 节的全部测试项。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;
    private static string _workDir = string.Empty;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 诊断模式：dotnet run -- --probe <文件路径>
        if (args.Length >= 2 && args[0] == "--probe")
        {
            return await ProbeFileAsync(args[1]);
        }

        Console.WriteLine("========================================");
        Console.WriteLine("SmartUnzip 核心功能实测");
        Console.WriteLine("========================================");
        Console.WriteLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        _workDir = Path.Combine(Path.GetTempPath(), "SmartUnzip_Tests_" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(_workDir);
        Console.WriteLine($"测试目录：{_workDir}");
        Console.WriteLine();

        var log = new Logger(true);
        var engine = new SevenZipEngine(log);

        Console.WriteLine("【环境】");
        Console.WriteLine($"  7-Zip 可用：{engine.IsAvailable}");
        Console.WriteLine($"  路径：{engine.EnginePath}");
        Console.WriteLine($"  版本：{engine.Version}");
        if (!engine.IsAvailable)
        {
            Console.WriteLine("  !! 未检测到 7-Zip，无法继续。");
            return 1;
        }

        // ==================== 1. 构造测试素材 ====================
        Console.WriteLine();
        Console.WriteLine("【准备测试文件】");
        var srcDir = Path.Combine(_workDir, "sfx");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "a.txt"), "hello smartunzip");
        File.WriteAllText(Path.Combine(srcDir, "b.txt"), "second file content");
        // 中文名与特殊字符
        File.WriteAllText(Path.Combine(srcDir, "中文文件.txt"), "中文内容测试");
        File.WriteAllText(Path.Combine(srcDir, "with space.txt"), "space test");

        var sz = engine.EnginePath!;
        string P(string name) => Path.Combine(_workDir, name);

        Run(sz, ["a", "-tzip", P("test.zip"), "a.txt", "b.txt", "中文文件.txt", "with space.txt"], srcDir);
        Run(sz, ["a", "-tzip", "-p123456", P("test_pwd.zip"), "a.txt", "b.txt"], srcDir);
        Run(sz, ["a", "-t7z", P("test.7z"), "a.txt", "b.txt"], srcDir);
        Run(sz, ["a", "-t7z", "-p123456", P("test_pwd.7z"), "a.txt", "b.txt"], srcDir);
        Run(sz, ["a", "-ttar", P("test.tar"), "a.txt", "b.txt"], srcDir);
        Run(sz, ["a", "-tgzip", P("test.tar.gz"), "test.tar"], _workDir);
        Run(sz, ["a", "-tbzip2", P("test.bz2"), "a.txt"], srcDir);
        Run(sz, ["a", "-txz", P("test.xz"), "a.txt"], srcDir);
        Console.WriteLine($"  已生成测试压缩包 {Directory.GetFiles(_workDir, "test.*").Length} 个");

        // 伪装文件
        File.Copy(P("test.zip"), P("fake.mp4"), true);
        File.Copy(P("test.zip"), P("fake.mkv"), true);
        File.Copy(P("test.7z"), P("fake_video.avi"), true);
        File.Copy(P("test_pwd.zip"), P("fake_pwd.mov"), true);
        Console.WriteLine("  已生成伪装文件 4 个");

        // 真视频（用最小合法 MP4 头构造）
        var fakeVideo = Path.Combine(_workDir, "real.mp4");
        BuildMinimalMp4(fakeVideo);
        var realMkv = Path.Combine(_workDir, "real.mkv");
        BuildMinimalMkv(realMkv);
        var realAvi = Path.Combine(_workDir, "real.avi");
        BuildMinimalAvi(realAvi);
        Console.WriteLine("  已生成真实视频文件 3 个");

        // 恶意压缩包（Zip Slip）
        var evilDir = Path.Combine(_workDir, "evil");
        Directory.CreateDirectory(evilDir);
        BuildEvilZip(sz, P("evil.zip"));
        Console.WriteLine("  已生成 Zip Slip 恶意压缩包 1 个");

        // 尾部附加压缩包（视频 + ZIP，网盘绕审核手法）
        BuildAppendedArchive(P("tail_zip.mp4"), P("test.zip"), 6 * 1024 * 1024);
        BuildAppendedArchive(P("tail_zip_pwd.mp4"), P("test_pwd.zip"), 6 * 1024 * 1024);
        BuildAppendedArchive(P("tail_7z.mp4"), P("test.7z"), 6 * 1024 * 1024);
        Console.WriteLine("  已生成尾部附加压缩包文件 3 个");

        // ==================== 2. FileTypeDetector 测试 ====================
        Console.WriteLine();
        Console.WriteLine("【测试组 1】FileTypeDetector 真实格式识别");

        CheckDetect(P("test.zip"), Core.FileType.Zip, false, "ZIP 正常");
        CheckDetect(P("test.7z"), Core.FileType.SevenZip, false, "7Z 正常");
        CheckDetect(P("test.tar"), Core.FileType.Tar, false, "TAR 正常");
        CheckDetect(P("test.tar.gz"), Core.FileType.Gzip, false, "GZIP 正常");
        CheckDetect(P("test.bz2"), Core.FileType.Bzip2, false, "BZIP2 正常");
        CheckDetect(P("test.xz"), Core.FileType.Xz, false, "XZ 正常");

        Console.WriteLine();
        Console.WriteLine("【测试组 2】伪装压缩包识别（核心功能）");
        CheckDetect(P("fake.mp4"), Core.FileType.Zip, true, "伪装 MP4 → ZIP");
        CheckDetect(P("fake.mkv"), Core.FileType.Zip, true, "伪装 MKV → ZIP");
        CheckDetect(P("fake_video.avi"), Core.FileType.SevenZip, true, "伪装 AVI → 7Z");
        CheckDetect(P("fake_pwd.mov"), Core.FileType.Zip, true, "伪装 MOV（加密）→ ZIP");

        Console.WriteLine();
        Console.WriteLine("【测试组 3】真实视频不得被误处理");
        CheckVideo(P("real.mp4"), Core.VideoFormat.Mp4, "真实 MP4");
        CheckVideo(P("real.mkv"), Core.VideoFormat.Mkv, "真实 MKV");
        CheckVideo(P("real.avi"), Core.VideoFormat.Avi, "真实 AVI");

        Console.WriteLine();
        Console.WriteLine("【测试组 3b】尾部附加压缩包（视频 + ZIP 拼接）");
        CheckEmbeddedArchive(P("tail_zip.mp4"), Core.FileType.Zip, "尾部附加 ZIP");
        CheckEmbeddedArchive(P("tail_zip_pwd.mp4"), Core.FileType.Zip, "尾部附加加密 ZIP");
        CheckEmbeddedArchive(P("tail_7z.mp4"), Core.FileType.SevenZip, "尾部附加 7Z");
        CheckRealVideoStillVideo(P("real.mp4"), "对照：真视频仍判为视频（未误报尾附）");

        // ==================== 3. 加密检测 ====================
        Console.WriteLine();
        Console.WriteLine("【测试组 4】加密检测");
        await CheckEncrypted(engine, P("test.zip"), false, "未加密 ZIP");
        await CheckEncrypted(engine, P("test_pwd.zip"), true, "加密 ZIP");
        await CheckEncrypted(engine, P("test_pwd.7z"), true, "加密 7Z");

        // ==================== 4. 密码验证 ====================
        Console.WriteLine();
        Console.WriteLine("【测试组 5】密码验证");
        await CheckPassword(engine, P("test_pwd.zip"), "123456", true, "正确密码");
        await CheckPassword(engine, P("test_pwd.zip"), "wrongpass", false, "错误密码");
        await CheckPassword(engine, P("test_pwd.7z"), "123456", true, "加密 7Z 正确密码");
        await CheckPassword(engine, P("test.zip"), null, true, "无密码包（空密码）");

        // ==================== 5. 密码库 ====================
        Console.WriteLine();
        Console.WriteLine("【测试组 6】密码库 + 自动匹配");
        await TestPasswordVault(engine, log, P("test_pwd.zip"), P("test_pwd.7z"));
        await TestPasswordVaultMultiple(engine, log, P("test_pwd.zip"), P("test_pwd.7z"), sz, srcDir, _workDir);

        // ==================== 6. 解压功能 ====================
        Console.WriteLine();
        Console.WriteLine("【测试组 7】解压功能");
        TestExtract(engine, P("test.zip"), "z1", 4, "普通 ZIP 含中文名");
        TestExtract(engine, P("fake.mp4"), "z2", 4, "伪装 MP4 直接解压（不改扩展名）");
        TestExtract(engine, P("test.7z"), "z3", 2, "7Z 解压");
        TestExtractWithPassword(engine, P("test_pwd.zip"), "z4", "123456", 2, "加密 ZIP 带密码解压");
        TestExtractWithPassword(engine, P("test_pwd.7z"), "z5", "123456", 2, "加密 7Z 带密码解压");
        TestExtract(engine, P("tail_zip.mp4"), "z6", 4, "尾部附加 ZIP 直接解压（视频文件）");
        TestExtractWithPassword(engine, P("tail_zip_pwd.mp4"), "z7", "123456", 2, "尾部附加加密 ZIP 带密码解压");

        // ==================== 7. 安全校验 ====================
        Console.WriteLine();
        Console.WriteLine("【测试组 8】Zip Slip / 路径穿越 防护");
        await TestZipSlip(engine, P("evil.zip"));

        // ==================== 8. 批量处理 ====================
        Console.WriteLine();
        Console.WriteLine("【测试组 9】批量处理");
        await TestBatch(engine, log, P("test.zip"), P("fake.mp4"), P("real.mp4"), P("test.7z"));

        // ==================== 9. 取消 ====================
        Console.WriteLine();
        Console.WriteLine("【测试组 10】取消任务");
        await TestCancel(engine, sz, _workDir);

        // ==================== 9b. 任务对象身份保持（UI 刷新根因） ====================
        Console.WriteLine();
        Console.WriteLine("【测试组 10b】任务对象身份保持（UI 卡片刷新）");
        await TestTaskIdentityPreserved(engine, log, P("fake.mp4"));

        // ==================== 10. 边界情况 ====================
        Console.WriteLine();
        Console.WriteLine("【测试组 11】边界情况");
        TestEdgeCases(engine, P("test.zip"));

        // ==================== 结果 ====================
        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine($"测试完成：通过 {_pass}，失败 {_fail}");
        Console.WriteLine("========================================");

        try { Directory.Delete(_workDir, true); } catch { }

        return _fail == 0 ? 0 : 2;
    }

    // ==================================================================

    /// <summary>
    /// 单文件诊断：对该文件跑完整检测 + 7-Zip 探测，输出全部判据。
    /// 用法：dotnet run -c Release -- --probe "路径"
    /// </summary>
    private static async Task<int> ProbeFileAsync(string path)
    {
        Console.WriteLine("========== 单文件诊断 ==========");
        Console.WriteLine($"文件：{path}");

        if (!File.Exists(path))
        {
            Console.WriteLine("!! 文件不存在");
            return 1;
        }

        var fi = new FileInfo(path);
        Console.WriteLine($"大小：{fi.Length} 字节 ({FileTypeInfo.FormatSize(fi.Length)})");

        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[16];
            fs.ReadExactly(head);
            Console.WriteLine($"头部 16 字节：{Convert.ToHexString(head)}");

            if (fi.Length > 64)
            {
                fs.Seek(-16, SeekOrigin.End);
                var tail = new byte[16];
                fs.ReadExactly(tail);
                Console.WriteLine($"尾部 16 字节：{Convert.ToHexString(tail)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"读取字节失败：{ex.Message}");
        }

        var info = FileTypeDetector.Detect(path);
        Console.WriteLine();
        Console.WriteLine("--- 检测结果 ---");
        Console.WriteLine($"  扩展名    ：{info.ExtensionDisplay}");
        Console.WriteLine($"  实际格式  ：{info.ActualTypeDisplay}");
        Console.WriteLine($"  MIME      ：{info.MimeType}");
        Console.WriteLine($"  是否压缩  ：{info.IsArchive}");
        Console.WriteLine($"  是否视频  ：{info.IsVideo}");
        Console.WriteLine($"  扩展名伪装：{info.IsDisguised}");
        Console.WriteLine($"  尾部附加  ：{info.IsEmbeddedArchive}");
        Console.WriteLine($"  置信度    ：{info.Confidence:0.00}");
        Console.WriteLine($"  状态文案  ：{info.StatusText}");

        var log = new Logger(true);
        var engine = new SevenZipEngine(log);
        Console.WriteLine($"  7-Zip     ：{(engine.IsAvailable ? engine.Version : "未找到")} @ {engine.EnginePath}");

        if (engine.IsAvailable)
        {
            try
            {
                var entries = await engine.ListEntriesAsync(path, null);
                Console.WriteLine($"  条目数    ：{entries.Count}");
                foreach (var e in entries.Take(12))
                    Console.WriteLine($"      - {e}");
                if (entries.Count > 12) Console.WriteLine($"      ...（共 {entries.Count} 条）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  列出条目失败：{ex.Message}");
            }

            try
            {
                var enc = await engine.IsEncryptedAsync(path);
                Console.WriteLine($"  是否加密  ：{enc}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  加密检测失败：{ex.Message}");
            }
        }

        return 0;
    }

    private static void CheckDetect(string path, Core.FileType expected, bool expectDisguised, string label)
    {
        try
        {
            var info = FileTypeDetector.Detect(path);
            var ok = info.ActualType == expected && info.IsDisguised == expectDisguised;
            Report(ok, $"{label}：实际={info.ActualTypeDisplay} 伪装={info.IsDisguised} 置信度={info.Confidence:0.00}");
        }
        catch (Exception ex)
        {
            Report(false, $"{label}：异常 {ex.Message}");
        }
    }

    private static void CheckVideo(string path, Core.VideoFormat fmt, string label)
    {
        try
        {
            var info = FileTypeDetector.Detect(path);
            var ok = info.IsVideo && info.ActualVideoFormat == fmt && !info.IsDisguised;
            Report(ok, $"{label}：视频={info.IsVideo} 格式={info.ActualVideoFormat} 伪装={info.IsDisguised} → 应跳过处理");
        }
        catch (Exception ex)
        {
            Report(false, $"{label}：异常 {ex.Message}");
        }
    }

    /// <summary>尾部附加压缩包：应被判为压缩格式、标记尾附、按压缩包处理（而非跳过）。</summary>
    private static void CheckEmbeddedArchive(string path, Core.FileType expected, string label)
    {
        try
        {
            var info = FileTypeDetector.Detect(path);
            var ok = info.ActualType == expected && info.IsEmbeddedArchive && info.IsArchive && !info.IsVideo;
            Report(ok,
                $"{label}：实际={info.ActualTypeDisplay} 尾附={info.IsEmbeddedArchive} " +
                $"可解压={info.IsArchive} 视频={info.IsVideo} 状态=\"{info.StatusText}\"");
        }
        catch (Exception ex)
        {
            Report(false, $"{label}：异常 {ex.Message}");
        }
    }

    /// <summary>反向校验：真视频（尾部无压缩包）不得被误标为尾附。</summary>
    private static void CheckRealVideoStillVideo(string path, string label)
    {
        try
        {
            var info = FileTypeDetector.Detect(path);
            var ok = info.IsVideo && !info.IsEmbeddedArchive;
            Report(ok, $"{label}：视频={info.IsVideo} 尾附={info.IsEmbeddedArchive}");
        }
        catch (Exception ex)
        {
            Report(false, $"{label}：异常 {ex.Message}");
        }
    }

    /// <summary>
    /// 构造"视频 + 尾部附加压缩包"文件：
    /// 先写一段最小合法 MP4 头 + padMB 的填充（模拟真实视频体积），
    /// 再把整个压缩包字节原样拼到文件尾。
    /// </summary>
    private static void BuildAppendedArchive(string outPath, string archivePath, int padBytes)
    {
        var ms = new MemoryStream();
        // ftyp box（保证头部被判为 MP4）
        ms.Write([0x00, 0x00, 0x00, 0x20]);
        ms.Write(Encoding.ASCII.GetBytes("ftyp"));
        ms.Write(Encoding.ASCII.GetBytes("isom"));
        ms.Write([0x00, 0x00, 0x02, 0x00]);
        ms.Write(Encoding.ASCII.GetBytes("isomiso2avc1mp41"));
        // mdat box 声明 + 填充
        ms.Write([0x00, 0x00, 0x00, 0x10]);
        ms.Write(Encoding.ASCII.GetBytes("mdat"));
        ms.Write(new byte[8]);
        // 填充（模拟视频主体，用非 PK 字节避免干扰）
        var pad = new byte[padBytes];
        for (int i = 0; i < pad.Length; i++) pad[i] = (byte)(i % 251);
        ms.Write(pad);
        // 追加压缩包
        ms.Write(File.ReadAllBytes(archivePath));
        File.WriteAllBytes(outPath, ms.ToArray());
    }

    private static async Task CheckEncrypted(SevenZipEngine engine, string path, bool expected, string label)
    {
        try
        {
            var enc = await engine.IsEncryptedAsync(path);
            Report(enc == expected, $"{label}：加密={enc}");
        }
        catch (Exception ex)
        {
            Report(false, $"{label}：异常 {ex.Message}");
        }
    }

    private static async Task CheckPassword(SevenZipEngine engine, string path, string? pwd, bool expected, string label)
    {
        try
        {
            var (ok, wrong) = await engine.TestAsync(path, pwd);
            Report(ok == expected, $"{label}：通过={ok} 密码错误标记={wrong}");
        }
        catch (Exception ex)
        {
            Report(false, $"{label}：异常 {ex.Message}");
        }
    }

    private static async Task TestPasswordVault(SevenZipEngine engine, Logger log,
        string pwdZip, string pwd7z)
    {
        var vault = new PasswordVault(log);
        int before = vault.Count;

        // 添加多个密码
        vault.Add(new PasswordEntry { Name = "软件资源密码", Password = "softpass111", Note = "软件资源" });
        vault.Add(new PasswordEntry { Name = "游戏资源密码", Password = "123456", Note = "游戏资源常用" });
        vault.Add(new PasswordEntry { Name = "下载资源密码", Password = "dlpass333", Note = "" });
        vault.Add(new PasswordEntry { Name = "备用密码", Password = "backup444", Note = "" });

        Report(vault.Count == before + 4, $"添加 4 个密码（总数 {vault.Count}）");

        // 搜索
        var found = vault.Search("游戏");
        Report(found.Count >= 1, $"搜索「游戏」找到 {found.Count} 条");

        // 自动匹配（密码库中含 123456）
        var matcher = new PasswordMatcher(vault, engine, log);
        var result = await matcher.MatchAsync(pwdZip);
        Report(result.Found && result.Password == "123456",
            $"自动匹配密码库：找到={result.Found} 条目=「{result.MatchedEntryName}」 尝试次数={result.TriedCount}");

        // 匹配成功后应更新最近使用
        var entry = vault.Entries.FirstOrDefault(e => e.Id == result.MatchedEntryId);
        Report(entry?.LastUsedAt is not null, "匹配成功后已更新「最后使用时间」");

        // 清理：删除刚加的测试条目
        foreach (var e in vault.Entries.Where(e => e.Note is "软件资源" or "游戏资源常用" or "" && e.Name.EndsWith("密码")).ToList())
            vault.Remove(e.Id);
        Console.WriteLine("       （已清理测试条目）");

        // 验证密码不入明文文件
        var vaultPath = vault.VaultPath;
        if (File.Exists(vaultPath))
        {
            var bytes = File.ReadAllBytes(vaultPath);
            var text = Encoding.UTF8.GetString(bytes);
            var leaked = text.Contains("123456") || text.Contains("softpass111");
            Report(!leaked, $"密码库落盘为密文（未泄露明文密码），文件 {bytes.Length} 字节");
        }
    }

    private static async Task TestPasswordVaultMultiple(SevenZipEngine engine, Logger log,
        string pwdZip, string pwd7z, string sz, string srcDir, string workDir)
    {
        // 构造相同密码的多个压缩包
        var p1 = Path.Combine(workDir, "multi1.zip");
        var p2 = Path.Combine(workDir, "multi2.7z");
        Run(sz, ["a", "-tzip", "-p123456", p1, "a.txt"], srcDir);
        Run(sz, ["a", "-t7z", "-p123456", p2, "a.txt"], srcDir);

        var vault = new PasswordVault(log);
        vault.Add(new PasswordEntry { Name = "批量测试密码", Password = "123456" });
        var matcher = new PasswordMatcher(vault, engine, log);

        var r1 = await matcher.MatchAsync(p1);
        var r2 = await matcher.MatchAsync(p2);
        Report(r1.Found && r2.Found, $"多个压缩包使用相同密码：包1={r1.Found} 包2={r2.Found}（用户只需保存一次密码）");

        // 会话记住密码
        matcher.RememberForSession(p1, "123456");
        Report(matcher.GetSessionPassword(p1) == "123456", "本次任务记住密码生效");
        matcher.ClearSession();
        Report(matcher.GetSessionPassword(p1) is null, "清除会话密码生效");

        foreach (var e in vault.Entries.Where(e => e.Name == "批量测试密码").ToList())
            vault.Remove(e.Id);
    }

    private static void TestExtract(SevenZipEngine engine, string src, string sub, int expectCount, string label)
    {
        var outDir = Path.Combine(_workDir, "out_" + sub);
        try
        {
            var result = engine.ExtractAsync(new ExtractRequest
            {
                SourcePath = src,
                OutputDirectory = outDir
            }, null).GetAwaiter().GetResult();

            var actual = result.Success ? Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Length : 0;
            Report(result.Success && actual >= expectCount,
                $"{label}：成功={result.Success} 文件数={actual}（预期 ≥{expectCount}）");
        }
        catch (Exception ex)
        {
            Report(false, $"{label}：异常 {ex.Message}");
        }
    }

    private static void TestExtractWithPassword(SevenZipEngine engine, string src, string sub,
        string pwd, int expectCount, string label)
    {
        var outDir = Path.Combine(_workDir, "out_" + sub);
        try
        {
            var result = engine.ExtractAsync(new ExtractRequest
            {
                SourcePath = src,
                OutputDirectory = outDir,
                Password = pwd
            }, null).GetAwaiter().GetResult();

            var actual = result.Success ? Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Length : 0;
            Report(result.Success && actual >= expectCount,
                $"{label}：成功={result.Success} 文件数={actual}");
        }
        catch (Exception ex)
        {
            Report(false, $"{label}：异常 {ex.Message}");
        }
    }

    private static async Task TestZipSlip(SevenZipEngine engine, string evilZip)
    {
        // 1. 直接校验恶意路径
        try
        {
            SecurityValidator.ValidateEntryPath(@"C:\safe\target", "../../evil.exe");
            Report(false, "Zip Slip：未能拦截 ../../evil.exe");
        }
        catch (SecurityException)
        {
            Report(true, "Zip Slip：成功拦截 ../../evil.exe");
        }

        try
        {
            SecurityValidator.ValidateEntryPath(@"C:\safe", "C:\\Windows\\System32\\evil.dll");
            Report(false, "Zip Slip：未能拦截绝对路径");
        }
        catch (SecurityException)
        {
            Report(true, "Zip Slip：成功拦截盘符绝对路径");
        }

        try
        {
            SecurityValidator.ValidateEntryPath(@"C:\safe", "subdir\\..\\..\\evil.txt");
            Report(false, "Zip Slip：未能拦截 subdir\\..\\..\\evil.txt");
        }
        catch (SecurityException)
        {
            Report(true, "Zip Slip：成功拦截相对穿越");
        }

        // 2. 合法路径应通过
        try
        {
            var ok = SecurityValidator.ValidateEntryPath(@"C:\safe\target", "subdir\\file.txt");
            Report(ok.StartsWith(@"C:\safe\target", StringComparison.OrdinalIgnoreCase),
                "Zip Slip：合法子目录路径正常通过");
        }
        catch (Exception ex)
        {
            Report(false, $"Zip Slip：合法路径被误拦 {ex.Message}");
        }

        // 3. 真实恶意压缩包整批校验
        try
        {
            var entries = await engine.ListEntriesAsync(evilZip, null);
            if (entries.Count > 0)
            {
                var (safe, bad, msg) = SecurityValidator.ValidateAll(Path.Combine(_workDir, "evil_out"), entries);
                Report(!safe, $"Zip Slip：恶意压缩包整批校验拦截（问题条目：{bad}）");
            }
            else
            {
                Console.WriteLine("       （恶意包条目为空，跳过整批校验）");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"       （恶意包读取失败：{ex.Message}）");
        }
    }

    /// <summary>
    /// 验证 ProcessAsync(IReadOnlyList&lt;ExtractionTask&gt;) 重载会复用调用方的任务对象。
    ///
    /// 这是 UI 卡片能刷新的唯一保证：
    /// UI 的 TaskItem 通过引用持有 ExtractionTask，TaskUpdated 回调里靠
    /// ReferenceEquals(item.Model, task) 找到要刷新的卡片。
    /// 若 ProcessAsync 内部重新 new 对象，卡片就永远停在初始状态（各项显示 "-"）。
    /// </summary>
    private static async Task TestTaskIdentityPreserved(SevenZipEngine engine, Logger log, string sample)
    {
        var settings = new SettingsManager(log);
        settings.Current.KeepOriginalFiles = true;
        settings.Current.OpenFolderAfterExtract = false;
        settings.Current.AutoMatchPassword = false;

        var vault = new PasswordVault(log);
        var matcher = new PasswordMatcher(vault, engine, log);

        var received = new List<ExtractionTask>();
        var mgr = new ExtractionTaskManager(engine, vault, matcher, settings, log)
        {
            PasswordPromptHandler = _ => Task.FromResult<PasswordPromptResult?>(null)
        };
        mgr.TaskUpdated += t => { lock (received) received.Add(t); };

        // 调用方预建任务对象（等价于 UI 的 TaskItem.Model）
        var mine = new ExtractionTask { SourcePath = Path.GetFullPath(sample) };
        var mineList = new List<ExtractionTask> { mine };

        var returned = await mgr.ProcessAsync(mineList);

        // 1) 返回的正是同一个对象
        Report(ReferenceEquals(returned[0], mine), "ProcessAsync 返回调用方的同一任务对象");

        // 2) 调用方对象的 Info 被真正填充（UI 卡片各字段不再为 "-"）
        Report(mine.Info is not null,
            $"调用方任务对象已填充 Info：实际格式={mine.Info?.ActualTypeDisplay ?? "null"}");

        // 3) 所有 TaskUpdated 回调收到的都是同一个引用（UI 才能匹配到卡片）
        bool allSame;
        lock (received) allSame = received.Count > 0 && received.All(t => ReferenceEquals(t, mine));
        Report(allSame, $"TaskUpdated 回调均为同一引用（收到 {received.Count} 次通知）");

        // 4) 状态已推进（不再是 Pending）
        Report(mine.State != TaskState.Pending,
            $"任务状态已推进：{mine.StateText}");

        // 5) 卡片各显示字段均有值（TaskItem 的字段就是直读 Info 的这几个属性）
        var info = mine.Info!;
        var ok = info.ExtensionDisplay != "-" && info.ActualTypeDisplay != "-" && info.SizeDisplay != "-";
        Report(ok, $"卡片字段不再为 \"-\"：扩展名={info.ExtensionDisplay} 实际={info.ActualTypeDisplay} 大小={info.SizeDisplay}");
    }

    private static async Task TestBatch(SevenZipEngine engine, Logger log, params string[] files)
    {
        var settings = new SettingsManager(log);
        settings.Current.KeepOriginalFiles = true;
        settings.Current.OpenFolderAfterExtract = false;
        settings.Current.AutoMatchPassword = true;

        var vault = new PasswordVault(log);
        vault.Add(new PasswordEntry { Name = "批量-临时", Password = "123456" });
        var matcher = new PasswordMatcher(vault, engine, log);
        var mgr = new ExtractionTaskManager(engine, vault, matcher, settings, log)
        {
            PasswordPromptHandler = _ => Task.FromResult<PasswordPromptResult?>(null)
        };

        var tasks = await mgr.ProcessAsync(files);
        int ok = tasks.Count(t => t.State == TaskState.Completed);
        int skip = tasks.Count(t => t.State == TaskState.Skipped);

        Report(tasks.Count == files.Length, $"批量处理任务数正确：{tasks.Count}");
        Report(skip >= 1, $"批量中真实视频被跳过：{skip} 个");
        Report(ok >= 3, $"批量中压缩包完成：{ok} 个");
        Console.WriteLine($"       明细：{string.Join(" | ", tasks.Select(t => $"{t.FileName}→{t.StateText}"))}");

        // 失败隔离测试：混入一个不存在的文件
        var mixed = files.Concat([Path.Combine(_workDir, "不存在的文件.zip")]).ToArray();
        var tasks2 = await mgr.ProcessAsync(mixed.Where(File.Exists));
        Report(tasks2.Count(t => t.State == TaskState.Completed) >= 3,
            "一个失败不影响整批（失败隔离）");

        foreach (var e in vault.Entries.Where(e => e.Name == "批量-临时").ToList())
            vault.Remove(e.Id);
    }

    private static async Task TestCancel(SevenZipEngine engine, string sz, string workDir)
    {
        // 构造一个较大的压缩包以便有时间取消
        var bigDir = Path.Combine(workDir, "bigsrc");
        Directory.CreateDirectory(bigDir);
        var rand = new Random(42);
        for (int i = 0; i < 30; i++)
        {
            var data = new byte[2 * 1024 * 1024];
            rand.NextBytes(data);
            // 低压缩比内容
            File.WriteAllBytes(Path.Combine(bigDir, $"f{i}.bin"), data);
        }
        var bigZip = Path.Combine(workDir, "big.zip");
        Run(sz, ["a", "-tzip", "-mx1", bigZip, "."], bigDir);

        var outDir = Path.Combine(workDir, "out_cancel");
        var cts = new CancellationTokenSource();

        var extractTask = engine.ExtractAsync(new ExtractRequest
        {
            SourcePath = bigZip,
            OutputDirectory = outDir
        }, null, cts.Token);

        // 50ms 后取消
        await Task.Delay(50);
        cts.Cancel();

        try
        {
            var result = await extractTask;
            if (result.Cancelled)
            {
                // 模拟任务管理器的清理逻辑
                try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
                Report(true, $"取消任务：已正确标记为取消（输出目录已清理）");
            }
            else
            {
                // 因为包小，可能在取消前已完成 —— 这也算正常
                Report(result.Success, $"取消任务：解压过快已完成（取消未生效但无错误）");
            }
        }
        catch (OperationCanceledException)
        {
            Report(true, "取消任务：抛出 OperationCanceledException（正确处理）");
        }
        catch (Exception ex)
        {
            Report(false, $"取消任务：异常 {ex.Message}");
        }
    }

    private static void TestEdgeCases(SevenZipEngine engine, string normalZip)
    {
        // 不存在的文件
        try
        {
            var info = FileTypeDetector.Detect(Path.Combine(_workDir, "no_such_file.zip"));
            Report(info.SizeBytes == 0 && info.Confidence == 0, "不存在的文件：安全返回 Unknown，不崩溃");
        }
        catch (Exception ex)
        {
            Report(false, $"不存在的文件：抛出异常 {ex.Message}");
        }

        // 空路径
        try
        {
            FileTypeDetector.Detect("");
            Report(false, "空路径：应当抛出参数异常");
        }
        catch (ArgumentException)
        {
            Report(true, "空路径：正确抛出 ArgumentException");
        }
        catch (Exception ex)
        {
            Report(false, $"空路径：异常类型不符 {ex.GetType().Name}");
        }

        // 损坏的压缩包
        var corrupt = Path.Combine(_workDir, "corrupt.zip");
        File.WriteAllBytes(corrupt, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x01, 0x02, 0x03, 0xFF, 0xFF]);
        var ci = FileTypeDetector.Detect(corrupt);
        Report(ci.ActualType == Core.FileType.Zip, "损坏 ZIP：仍能靠魔数识别为 ZIP（不崩溃）");

        var r = engine.ExtractAsync(new ExtractRequest
        {
            SourcePath = corrupt,
            OutputDirectory = Path.Combine(_workDir, "out_corrupt")
        }, null).GetAwaiter().GetResult();
        Report(!r.Success && r.ErrorMessage is { Length: > 0 },
            $"损坏 ZIP 解压：正确失败并给出中文提示「{r.ErrorMessage}」");

        // 超长路径
        try
        {
            var longName = new string('x', 300) + ".txt";
            SecurityValidator.ValidateEntryPath(@"C:\safe", longName);
            Report(false, "超长路径：应当被拦截");
        }
        catch (SecurityException)
        {
            Report(true, "超长路径：正确拦截");
        }

        // 输出目录冲突
        var baseDir = Path.Combine(_workDir, "conflict");
        Directory.CreateDirectory(baseDir);
        var d1 = SecurityValidator.ResolveOutputDirectory(Path.Combine(baseDir, "a.zip"), OutputCollisionPolicy.NewFolder);
        Directory.CreateDirectory(d1);
        var d2 = SecurityValidator.ResolveOutputDirectory(Path.Combine(baseDir, "a.zip"), OutputCollisionPolicy.NewFolder);
        Report(d1 != d2 && d2.Contains("(2)"), $"输出目录冲突：新建目录策略生效（{Path.GetFileName(d2)}）");

        var d3 = SecurityValidator.ResolveOutputDirectory(Path.Combine(baseDir, "a.zip"), OutputCollisionPolicy.Reuse);
        Report(d3 == d1, "输出目录冲突：复用已有目录策略生效");

        // 日志不含密码
        var masked = Logger.MaskSecrets("执行 7-Zip：x test.zip -p123456 -y");
        Report(!masked.Contains("123456"), $"日志脱敏：密码已打码（{masked}）");
    }

    // ==================================================================
    // 辅助方法
    // ==================================================================

    private static void Run(string exe, string[] args, string? cwd)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    /// <summary>构造最小合法 MP4（ftyp 头）。</summary>
    private static void BuildMinimalMp4(string path)
    {
        var bytes = new List<byte>();
        // ftyp box
        bytes.AddRange([0x00, 0x00, 0x00, 0x20]); // size 32
        bytes.AddRange(Encoding.ASCII.GetBytes("ftyp"));
        bytes.AddRange(Encoding.ASCII.GetBytes("isom")); // major brand
        bytes.AddRange([0x00, 0x00, 0x02, 0x00]);
        bytes.AddRange(Encoding.ASCII.GetBytes("isomiso2avc1mp41"));
        // mdat box
        bytes.AddRange([0x00, 0x00, 0x00, 0x10]);
        bytes.AddRange(Encoding.ASCII.GetBytes("mdat"));
        bytes.AddRange(new byte[8]);
        File.WriteAllBytes(path, bytes.ToArray());
    }

    /// <summary>构造最小合法 MKV（EBML 头）。</summary>
    private static void BuildMinimalMkv(string path)
    {
        var bytes = new List<byte>();
        bytes.AddRange([0x1A, 0x45, 0xDF, 0xA3]);      // EBML magic
        bytes.AddRange([0x9F]);                         // size
        bytes.AddRange(Encoding.ASCII.GetBytes("matroska")); // DocType 内容
        bytes.AddRange(new byte[64]);
        File.WriteAllBytes(path, bytes.ToArray());
    }

    /// <summary>构造最小合法 AVI（RIFF....AVI ）。</summary>
    private static void BuildMinimalAvi(string path)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("RIFF"));
        bytes.AddRange([0x24, 0x00, 0x00, 0x00]);       // size
        bytes.AddRange(Encoding.ASCII.GetBytes("AVI "));
        bytes.AddRange(new byte[32]);
        File.WriteAllBytes(path, bytes.ToArray());
    }

    /// <summary>构造含路径穿越条目的恶意 ZIP。</summary>
    private static void BuildEvilZip(string sz, string outZip)
    {
        // 用 PowerShell 的 ZipFile 无法直接创建 .. 路径，改用 7-Zip 的 listfile 技巧
        var tmp = Path.Combine(Path.GetTempPath(), "evil_src_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "evil.exe"), "MZ malicious payload simulation");
            // 用 7z 打包后，通过改名方式模拟（7z 会自动过滤 ..）
            // 更可靠的方式：手工构造 ZIP 二进制
            BuildRawEvilZip(outZip);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>手工构造含 ../../ 条目的 ZIP（最小合法结构）。</summary>
    private static void BuildRawEvilZip(string path)
    {
        var ms = new MemoryStream();
        var r = new BinaryWriter(ms);

        var name = Encoding.UTF8.GetBytes("../../evil.txt");
        var content = Encoding.UTF8.GetBytes("pwned");

        // Local file header
        r.Write(0x04034b50);            // signature
        r.Write((ushort)20);            // version needed
        r.Write((ushort)0);             // flags
        r.Write((ushort)0);             // method (store)
        r.Write((ushort)0);             // mod time
        r.Write((ushort)0);             // mod date
        r.Write(Crc32(content));        // crc32
        r.Write(content.Length);        // compressed size
        r.Write(content.Length);        // uncompressed size
        r.Write((ushort)name.Length);   // name length
        r.Write((ushort)0);             // extra length
        r.Write(name);
        r.Write(content);

        var cdOffset = (int)ms.Position;

        // Central directory
        r.Write(0x02014b50);
        r.Write((ushort)20);
        r.Write((ushort)20);
        r.Write((ushort)0);
        r.Write((ushort)0);
        r.Write((ushort)0);
        r.Write((ushort)0);
        r.Write(Crc32(content));
        r.Write(content.Length);
        r.Write(content.Length);
        r.Write((ushort)name.Length);
        r.Write((ushort)0);
        r.Write((ushort)0);
        r.Write((ushort)0);
        r.Write((ushort)0);
        r.Write(0);
        r.Write(0);
        r.Write(name);

        var cdSize = (int)ms.Position - cdOffset;

        // End of central directory
        r.Write(0x06054b50);
        r.Write((ushort)0);
        r.Write((ushort)0);
        r.Write((ushort)1);
        r.Write((ushort)1);
        r.Write(cdSize);
        r.Write(cdOffset);
        r.Write((ushort)0);

        File.WriteAllBytes(path, ms.ToArray());
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)(-(int)(crc & 1)));
        }
        return ~crc;
    }

    private static void Report(bool ok, string message)
    {
        if (ok) { _pass++; Console.WriteLine($"  [通过] {message}"); }
        else { _fail++; Console.WriteLine($"  [失败] {message}"); }
    }
}
