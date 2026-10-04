namespace SmartUnzip.Core;

/// <summary>
/// 文件真实格式枚举。
/// 这是 Flux 的基础判据：绝不依赖扩展名。
/// </summary>
public enum FileType
{
    Unknown,
    Video,
    Zip,
    Rar,
    SevenZip,
    Tar,
    Gzip,
    Bzip2,
    Xz
}

/// <summary>
/// 具体的视频格式（仅用于显示，本体归为 FileType.Video）。
/// </summary>
public enum VideoFormat
{
    None,
    Mp4,
    Mkv,
    Avi,
    Mov,
    Webm
}

/// <summary>
/// 文件检测结果。字段对齐需求文档第五节。
/// </summary>
public sealed class FileTypeInfo
{
    /// <summary>文件名（含扩展名，不含目录）。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>完整路径。</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>扩展名（小写，不含点）。无扩展名时为空串。</summary>
    public string Extension { get; init; } = string.Empty;

    /// <summary>扩展名大写形式，用于显示。无扩展名时为 "（无）"。</summary>
    public string ExtensionDisplay =>
        string.IsNullOrEmpty(Extension) ? "（无扩展名）" : Extension.ToUpperInvariant();

    /// <summary>实际格式（魔数判定结果）。</summary>
    public FileType ActualType { get; init; } = FileType.Unknown;

    /// <summary>若实际是视频，给出具体视频格式。</summary>
    public VideoFormat ActualVideoFormat { get; init; } = VideoFormat.None;

    /// <summary>MIME 类型。</summary>
    public string MimeType { get; init; } = "application/octet-stream";

    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>是否属于受支持的压缩格式。</summary>
    public bool IsArchive => ActualType is FileType.Zip or FileType.Rar or FileType.SevenZip
        or FileType.Tar or FileType.Gzip or FileType.Bzip2 or FileType.Xz;

    /// <summary>是否真实视频文件。</summary>
    public bool IsVideo => ActualType == FileType.Video;

    /// <summary>是否存在扩展名伪装（扩展名声称的格式 ≠ 实际格式，且实际为压缩格式）。</summary>
    public bool IsDisguised { get; init; }

    /// <summary>
    /// 是否为"尾部附加压缩包"——文件头是真视频（或其他格式），尾部拼接了完整压缩包。
    /// 网盘绕审核的常见分发手法，必须按压缩包处理。
    /// </summary>
    public bool IsEmbeddedArchive { get; init; }

    /// <summary>是否加密（需要密码）。由后续 7-Zip 探测填充。</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>检测置信度 0.0 ~ 1.0。</summary>
    public double Confidence { get; init; }

    /// <summary>人类可读的大小。</summary>
    public string SizeDisplay => FormatSize(SizeBytes);

    /// <summary>实际格式的显示名。</summary>
    public string ActualTypeDisplay => ActualType switch
    {
        FileType.Zip => "ZIP",
        FileType.Rar => "RAR",
        FileType.SevenZip => "7Z",
        FileType.Tar => "TAR",
        FileType.Gzip => "GZIP",
        FileType.Bzip2 => "BZIP2",
        FileType.Xz => "XZ",
        FileType.Video => ActualVideoFormat switch
        {
            VideoFormat.Mp4 => "MP4",
            VideoFormat.Mkv => "MKV",
            VideoFormat.Avi => "AVI",
            VideoFormat.Mov => "MOV",
            VideoFormat.Webm => "WEBM",
            _ => "视频"
        },
        _ => "未知"
    };

    /// <summary>面向用户的状态描述（中文）。</summary>
    public string StatusText
    {
        get
        {
            if (ActualType == FileType.Unknown)
                return "无法识别的格式";

            if (IsVideo)
                return "普通视频文件，无需处理";

            if (IsEmbeddedArchive)
                return $"检测到尾部附加{ActualTypeDisplay}（伪装视频）";

            if (IsDisguised)
                return $"检测到扩展名伪装（{ExtensionDisplay} → {ActualTypeDisplay}）";

            if (IsArchive)
                return "压缩包，可解压";

            return "未知";
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < units.Length - 1)
        {
            size /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} B" : $"{size:0.##} {units[i]}";
    }

    public override string ToString() =>
        $"{FileName} | 扩展名={ExtensionDisplay} 实际={ActualTypeDisplay} 伪装={IsDisguised} 尾附={IsEmbeddedArchive} 加密={IsEncrypted} 置信度={Confidence:0.00}";
}
