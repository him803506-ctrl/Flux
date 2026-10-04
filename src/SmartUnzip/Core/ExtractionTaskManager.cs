using System.IO;

namespace SmartUnzip.Core;

/// <summary>任务状态。</summary>
public enum TaskState
{
    Pending,
    Detecting,
    WaitingPassword,
    Extracting,
    Completed,
    Skipped,
    Failed,
    Cancelled
}

/// <summary>
/// 一个文件的处理任务。
/// </summary>
public sealed class ExtractionTask
{
    public required string SourcePath { get; init; }

    public FileTypeInfo? Info { get; set; }

    public TaskState State { get; set; } = TaskState.Pending;

    public double Progress { get; set; }

    public long ProcessedBytes { get; set; }

    public long TotalBytes { get; set; }

    public string? OutputDirectory { get; set; }

    public string? ErrorMessage { get; set; }

    public int ExtractedFileCount { get; set; }

    public bool UsedPassword { get; set; }

    public string FileName => Path.GetFileName(SourcePath);

    public string StateText => State switch
    {
        TaskState.Pending => "等待处理",
        TaskState.Detecting => "检测中",
        TaskState.WaitingPassword => "等待密码",
        TaskState.Extracting => "处理中",
        TaskState.Completed => UsedPassword ? "已完成（已自动匹配密码）" : "已完成",
        TaskState.Skipped => "无需处理",
        TaskState.Failed => "失败",
        TaskState.Cancelled => "已取消",
        _ => "未知"
    };
}

/// <summary>
/// 请求用户输入密码的交互回调。返回 (密码, 是否保存到密码库, 密码名称, 备注, 是否本次记住)。
/// 返回 null 表示用户取消。
/// </summary>
public sealed class PasswordPromptResult
{
    public string Password { get; init; } = string.Empty;
    public bool SaveToVault { get; init; }
    public string EntryName { get; init; } = string.Empty;
    public string EntryNote { get; init; } = string.Empty;
    public bool RememberForSession { get; init; }
}

/// <summary>
/// 批量解压任务管理器。
///
/// 原则：
///  - 一个文件失败不影响整批继续。
///  - 支持随时取消；取消后清理临时文件，不破坏原文件。
/// </summary>
public sealed class ExtractionTaskManager
{
    private readonly IArchiveEngine _engine;
    private readonly PasswordVault _vault;
    private readonly PasswordMatcher _matcher;
    private readonly SettingsManager _settings;
    private readonly Logger _log;

    /// <summary>需要用户输入密码时触发。UI 层负责实现对话框。</summary>
    public Func<ExtractionTask, Task<PasswordPromptResult?>>? PasswordPromptHandler { get; set; }

    /// <summary>任务状态变化通知。</summary>
    public Action<ExtractionTask>? TaskUpdated { get; set; }

    public ExtractionTaskManager(IArchiveEngine engine, PasswordVault vault, PasswordMatcher matcher,
        SettingsManager settings, Logger log)
    {
        _engine = engine;
        _vault = vault;
        _matcher = matcher;
        _settings = settings;
        _log = log;
    }

    /// <summary>
    /// 处理一批文件（按路径创建新任务）。返回所有任务（含跳过与失败）。
    /// </summary>
    public Task<IReadOnlyList<ExtractionTask>> ProcessAsync(
        IEnumerable<string> filePaths,
        IProgress<ExtractionTask>? progress = null,
        CancellationToken ct = default)
    {
        var tasks = filePaths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new ExtractionTask { SourcePath = Path.GetFullPath(p) })
            .ToList();

        return ProcessAsync(tasks, progress, ct);
    }

    /// <summary>
    /// 处理一批任务（复用调用方已有的 ExtractionTask 对象）。
    ///
    /// 重要：UI 层必须走这个重载。
    /// UI 的卡片（TaskItem）通过引用持有 ExtractionTask，
    /// 若这里重新 new 一批对象，TaskUpdated 回调里的 ReferenceEquals 匹配不上，
    /// 卡片就永远不会刷新（表现为各项显示 "-"、状态停在"等待处理"）。
    /// </summary>
    public async Task<IReadOnlyList<ExtractionTask>> ProcessAsync(
        IReadOnlyList<ExtractionTask> tasks,
        IProgress<ExtractionTask>? progress = null,
        CancellationToken ct = default)
    {
        foreach (var task in tasks)
        {
            if (ct.IsCancellationRequested)
            {
                task.State = TaskState.Cancelled;
                Notify(task, progress);
                continue;
            }

            try
            {
                await ProcessOneAsync(task, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                task.State = TaskState.Cancelled;
                task.ErrorMessage = "已取消";
            }
            catch (Exception ex)
            {
                task.State = TaskState.Failed;
                task.ErrorMessage = $"未预期的错误：{ex.Message}";
                _log.Error($"处理 {task.FileName} 时发生异常：{ex.Message}");
            }
            Notify(task, progress);
        }

        return tasks;
    }

    private async Task ProcessOneAsync(ExtractionTask task, IProgress<ExtractionTask>? progress,
        CancellationToken ct)
    {
        task.State = TaskState.Detecting;
        Notify(task, progress);

        // 1. 检测真实格式
        var info = FileTypeDetector.Detect(task.SourcePath);
        task.Info = info;
        _log.Info($"检测文件：{info.FileName}");
        _log.Info($"实际格式：{info.ActualTypeDisplay}");

        if (info.IsDisguised)
            _log.Info("检测到扩展名伪装");

        // 2. 真正视频 → 跳过，不做任何修改
        if (info.IsVideo)
        {
            task.State = TaskState.Skipped;
            _log.Info("确认为普通视频文件，跳过处理");
            Notify(task, progress);
            return;
        }

        // 3. 非压缩格式 → 跳过
        if (!info.IsArchive)
        {
            task.State = TaskState.Skipped;
            task.ErrorMessage = "无法识别的格式，已跳过";
            _log.Info("非压缩格式，跳过处理");
            Notify(task, progress);
            return;
        }

        // 4. 安全预检：先列出所有条目，校验路径
        var dryRunEntries = await SafeListEntriesAsync(task, ct).ConfigureAwait(false);
        _log.Debug($"列出条目：{(dryRunEntries is null ? "失败/加密" : dryRunEntries.Count + " 条")}");

        // 5. 判断加密
        bool encrypted = false;
        try
        {
            encrypted = await _engine.IsEncryptedAsync(task.SourcePath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn($"加密检测失败：{ex.Message}");
        }

        info.IsEncrypted = encrypted;
        _log.Debug($"加密状态：{encrypted}");

        string? password = null;

        if (encrypted)
        {
            _log.Info("检测到加密压缩包");
            task.State = TaskState.WaitingPassword;
            Notify(task, progress);

            if (_settings.Current.AutoMatchPassword)
            {
                var matchProgress = new Progress<string>(msg => _log.Debug(msg));
                var match = await _matcher.MatchAsync(task.SourcePath, matchProgress, ct).ConfigureAwait(false);

                if (match.Found)
                {
                    password = match.Password;
                    task.UsedPassword = true;
                    task.State = TaskState.Extracting;
                    _log.Info("密码匹配成功");
                    Notify(task, progress);
                }
                else
                {
                    _log.Info("密码库匹配失败，需要用户手动输入");
                }
            }

            // 匹配失败 → 请求用户输入
            if (password is null)
            {
                var prompt = PasswordPromptHandler;
                if (prompt is null)
                {
                    task.State = TaskState.Failed;
                    task.ErrorMessage = "压缩包已加密，且无密码输入界面。";
                    Notify(task, progress);
                    return;
                }

                var input = await prompt(task).ConfigureAwait(false);
                if (input is null || string.IsNullOrEmpty(input.Password))
                {
                    task.State = TaskState.Cancelled;
                    task.ErrorMessage = "用户取消输入密码";
                    _log.Info("用户取消密码输入");
                    Notify(task, progress);
                    return;
                }

                password = input.Password;
                task.UsedPassword = true;

                // 校验用户输入
                bool valid;
                try
                {
                    valid = await _matcher.VerifyAsync(task.SourcePath, password, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }

                if (!valid)
                {
                    task.State = TaskState.Failed;
                    task.ErrorMessage = "密码错误，无法解压。";
                    _log.Info("用户输入的密码校验失败");
                    Notify(task, progress);
                    return;
                }

                if (input.RememberForSession && _settings.Current.RememberPasswordForSession)
                    _matcher.RememberForSession(task.SourcePath, password);

                if (input.SaveToVault)
                {
                    _vault.Add(new PasswordEntry
                    {
                        Name = string.IsNullOrWhiteSpace(input.EntryName)
                            ? Path.GetFileNameWithoutExtension(task.FileName) + " 密码"
                            : input.EntryName,
                        Password = password,
                        Note = input.EntryNote
                    });
                }
            }
        }

        // 6. 输出目录
        var outputDir = ResolveOutputDirectory(task.SourcePath);
        task.OutputDirectory = outputDir;
        task.State = TaskState.Extracting;
        _log.Debug($"输出目录：{outputDir}");
        Notify(task, progress);

        // 7. 安全检查（在真正解压前，用列出的条目做路径校验）
        if (dryRunEntries is { Count: > 0 })
        {
            var (safe, badEntry, message) = SecurityValidator.ValidateAll(outputDir, dryRunEntries);
            _log.Debug($"安全校验：safe={safe} bad={badEntry}");
            if (!safe)
            {
                task.State = TaskState.Failed;
                task.ErrorMessage = message ?? $"不安全条目：{badEntry}";
                _log.Error($"安全校验未通过：{message}");
                Notify(task, progress);
                return;
            }
        }

        // 8. 解压
        _log.Info("调用 7-Zip 开始解压");
        var progressReporter = new Progress<ExtractProgress>(p =>
        {
            task.Progress = p.Percent;
            task.ProcessedBytes = p.ProcessedBytes;
            task.TotalBytes = p.TotalBytes;
            Notify(task, progress);
        });

        ExtractResult result;
        try
        {
            result = await _engine.ExtractAsync(new ExtractRequest
            {
                SourcePath = task.SourcePath,
                OutputDirectory = outputDir,
                Password = password,
                Overwrite = false
            }, progressReporter, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            task.State = TaskState.Cancelled;
            task.ErrorMessage = "已取消";
            CleanupOnCancel(outputDir);
            _log.Info("解压已取消");
            Notify(task, progress);
            return;
        }

        if (result.Cancelled)
        {
            task.State = TaskState.Cancelled;
            task.ErrorMessage = "已取消";
            CleanupOnCancel(outputDir);
            Notify(task, progress);
            return;
        }

        if (!result.Success)
        {
            task.State = TaskState.Failed;
            task.ErrorMessage = result.WrongPassword ? "密码错误，无法解压。" : result.ErrorMessage;
            _log.Error($"解压失败：{result.ErrorMessage}");
            Notify(task, progress);
            return;
        }

        // 9. 解压后检查符号链接逃逸
        var links = SecurityValidator.FindEscapingSymlinks(outputDir);
        if (links.Count > 0)
        {
            _log.Warn($"解压后发现 {links.Count} 个可能越界的符号链接");
        }

        task.Progress = 100;
        task.ExtractedFileCount = result.FileCount;
        task.State = TaskState.Completed;
        _log.Info($"解压完成，共 {result.FileCount} 个条目");

        // 10. 删除原文件（仅当用户明确关闭"保留原始文件"）
        if (!_settings.Current.KeepOriginalFiles)
        {
            try
            {
                // 安全起见：改为移动到回收站而非直接删除
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    task.SourcePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                _log.Info("原文件已移入回收站");
            }
            catch (Exception ex)
            {
                _log.Warn($"原文件移入回收站失败：{ex.Message}");
            }
        }

        Notify(task, progress);
    }

    private async Task<IReadOnlyList<string>?> SafeListEntriesAsync(ExtractionTask task, CancellationToken ct)
    {
        try
        {
            var entries = await _engine.ListEntriesAsync(task.SourcePath, null, ct).ConfigureAwait(false);
            return entries.Count > 0 ? entries : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Debug($"列出条目失败（可能在解压前无法读取）：{ex.Message}");
            return null;
        }
    }

    private string ResolveOutputDirectory(string sourcePath)
    {
        var s = _settings.Current;
        string baseOut;

        if (s.OutputMode == "custom" && !string.IsNullOrWhiteSpace(s.CustomOutputDirectory))
        {
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            baseOut = Path.Combine(s.CustomOutputDirectory, name);
        }
        else
        {
            var dir = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(name)) name = "Flux_输出";
            baseOut = Path.Combine(dir, name);
        }

        if (!Directory.Exists(baseOut)) return baseOut;

        return _settings.ResolveCollisionPolicy() switch
        {
            OutputCollisionPolicy.Reuse => baseOut,
            _ => MakeUnique(baseOut)
        };
    }

    private static string MakeUnique(string basePath)
    {
        for (int i = 2; i < 10000; i++)
        {
            var c = $"{basePath} ({i})";
            if (!Directory.Exists(c)) return c;
        }
        return $"{basePath} ({Guid.NewGuid():N})";
    }

    /// <summary>取消后清理：删除本次解压产生的（可能不完整的）输出目录。</summary>
    private void CleanupOnCancel(string outputDir)
    {
        try
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
                _log.Info($"已清理未完成的输出目录：{outputDir}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"清理输出目录失败：{ex.Message}");
        }
    }

    private void Notify(ExtractionTask task, IProgress<ExtractionTask>? progress)
    {
        try
        {
            progress?.Report(task);
            TaskUpdated?.Invoke(task);
        }
        catch { }
    }
}
