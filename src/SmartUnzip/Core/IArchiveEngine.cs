namespace SmartUnzip.Core;

/// <summary>
/// 解压请求。
/// </summary>
public sealed class ExtractRequest
{
    /// <summary>源压缩包路径（可以是伪装扩展名）。</summary>
    public required string SourcePath { get; init; }

    /// <summary>输出目录。</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>解压密码（可空，表示无密码）。</summary>
    public string? Password { get; init; }

    /// <summary>是否覆盖已存在文件。</summary>
    public bool Overwrite { get; init; }
}

/// <summary>
/// 解压进度。
/// </summary>
public sealed class ExtractProgress
{
    public double Percent { get; init; }
    public long ProcessedBytes { get; init; }
    public long TotalBytes { get; init; }
    public string? CurrentFile { get; init; }

    public string ProcessedDisplay => FileTypeInfo.FormatSize(ProcessedBytes);
    public string TotalDisplay => FileTypeInfo.FormatSize(TotalBytes);
}

/// <summary>
/// 解压结果。
/// </summary>
public sealed class ExtractResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string? ErrorMessage { get; init; }
    public bool WrongPassword { get; init; }
    public bool Cancelled { get; init; }
    public string OutputDirectory { get; init; } = string.Empty;
    public int FileCount { get; init; }
}

/// <summary>
/// 解压引擎抽象。未来可增加 SevenZipEngine 之外的实现而不改动 UI。
/// </summary>
public interface IArchiveEngine
{
    /// <summary>引擎显示名。</summary>
    string Name { get; }

    /// <summary>引擎是否可用（依赖已就绪）。</summary>
    bool IsAvailable { get; }

    /// <summary>引擎可执行文件路径。</summary>
    string? EnginePath { get; }

    /// <summary>引擎版本字符串。</summary>
    string? Version { get; }

    /// <summary>列出压缩包内所有条目路径。</summary>
    Task<IReadOnlyList<string>> ListEntriesAsync(string archivePath, string? password, CancellationToken ct = default);

    /// <summary>测试压缩包是否可正常解压（用于验证密码）。</summary>
    Task<(bool ok, bool wrongPassword)> TestAsync(string archivePath, string? password, CancellationToken ct = default);

    /// <summary>判断压缩包是否加密。</summary>
    Task<bool> IsEncryptedAsync(string archivePath, CancellationToken ct = default);

    /// <summary>解压。</summary>
    Task<ExtractResult> ExtractAsync(ExtractRequest request, IProgress<ExtractProgress>? progress,
        CancellationToken ct = default);
}
