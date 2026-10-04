using System.IO;

namespace SmartUnzip.Core;

/// <summary>
/// 独立文件类型检测模块。
/// 只读文件头（默认 64KB），不加载整个文件到内存。
/// 绝不根据扩展名判断真实格式。
/// </summary>
public static class FileTypeDetector
{
    /// <summary>读取文件头字节数。足够覆盖 TAR(offset 257) 与所有常见魔数。</summary>
    private const int HeaderSize = 64 * 1024;

    /// <summary>
    /// 读取文件尾字节数。用于识别"视频/音频 + 尾部附加压缩包"这类伪装分发格式
    /// （网盘常见的绕过审核手法：前面是真视频，ZIP 数据的 EOCD 在文件末尾）。
    /// ZIP 中央目录结束记录（EOCD）最大 65557 字节，取 128KB 足够。
    /// </summary>
    private const int TailSize = 128 * 1024;

    /// <summary>尾部扫描的最小文件大小（太小的文件头部已覆盖，无需读尾）。</summary>
    private const long MinSizeForTailScan = 256 * 1024;

    /// <summary>扩展名 → 声称的格式。用于伪装判定。</summary>
    private static readonly Dictionary<string, FileType> ExtensionClaims = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zip"] = FileType.Zip,
        ["zipx"] = FileType.Zip,
        ["jar"] = FileType.Zip,
        ["rar"] = FileType.Rar,
        ["r00"] = FileType.Rar,
        ["7z"] = FileType.SevenZip,
        ["tar"] = FileType.Tar,
        ["gz"] = FileType.Gzip,
        ["tgz"] = FileType.Gzip,
        ["bz2"] = FileType.Bzip2,
        ["tbz2"] = FileType.Bzip2,
        ["tbz"] = FileType.Bzip2,
        ["xz"] = FileType.Xz,
        ["txz"] = FileType.Xz,
    };

    /// <summary>视频扩展名集合。用于伪装判定。</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "avi", "mov", "webm", "flv", "wmv", "m4v", "mpg", "mpeg", "ts", "rmvb", "3gp"
    };

    /// <summary>
    /// 检测单个文件。
    /// </summary>
    public static FileTypeInfo Detect(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("文件路径不能为空", nameof(filePath));

        var fullPath = Path.GetFullPath(filePath);
        var fileName = Path.GetFileName(fullPath);
        var ext = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();

        long size = 0;
        try
        {
            var fi = new FileInfo(fullPath);
            if (fi.Exists) size = fi.Length;
        }
        catch
        {
            // 大小读取失败不影响格式判定
        }

        byte[] header;
        try
        {
            header = ReadHeader(fullPath);
        }
        catch
        {
            // 读取失败（文件被独占占用 / 无权限 / 路径异常）→ 返回 Unknown，由调用方决定如何处理
            return new FileTypeInfo
            {
                FileName = fileName,
                FullPath = fullPath,
                Extension = ext,
                ActualType = FileType.Unknown,
                SizeBytes = size,
                Confidence = 0.0,
                MimeType = "application/octet-stream",
                IsDisguised = false
            };
        }

        var (type, videoFmt, mime, confidence) = Probe(header, ext);

        var disguised = IsDisguise(ext, type);

        // ---- 尾部附加压缩包检测 ----
        // 典型场景：xxx.mp4 前面是真 MP4、尾部拼接了 ZIP（网盘绕审核的常见做法）。
        // 此时头部魔数判定为 MP4，会被当作真视频跳过，但用户实际想要的是解压。
        // 策略：头部判定为"视频"时，再扫一遍文件尾；若尾部存在 zip 头，则改判为压缩包。
        var embedded = false;
        if (type == FileType.Video && size >= MinSizeForTailScan)
        {
            try
            {
                var tail = ReadTail(fullPath, size);
                if (tail is not null && TryDetectAppendedArchive(tail, out var appendedType, out var appendedMime))
                {
                    type = appendedType;
                    videoFmt = VideoFormat.None;
                    mime = appendedMime;
                    confidence = 0.85;
                    embedded = true;
                    disguised = true;
                }
            }
            catch
            {
                // 尾部读取失败不影响已得到的头部判定
            }
        }

        return new FileTypeInfo
        {
            FileName = fileName,
            FullPath = fullPath,
            Extension = ext,
            ActualType = type,
            ActualVideoFormat = videoFmt,
            MimeType = mime,
            SizeBytes = size,
            IsDisguised = disguised,
            IsEmbeddedArchive = embedded,
            Confidence = confidence
        };
    }

    /// <summary>批量检测（并行，适合大量文件）。</summary>
    public static IReadOnlyList<FileTypeInfo> DetectMany(IEnumerable<string> filePaths)
    {
        var list = filePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
        var results = new FileTypeInfo[list.Count];
        Parallel.For(0, list.Count, i =>
        {
            try
            {
                results[i] = Detect(list[i]);
            }
            catch
            {
                // 单个文件失败不能影响整批
            }
        });
        return results.Where(r => r is not null).ToList();
    }

    /// <summary>
    /// 读取文件头。文件小于 HeaderSize 时读取全部。
    /// </summary>
    private static byte[] ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
        var len = (int)Math.Min(HeaderSize, fs.Length);
        var buffer = new byte[len];
        int read = 0;
        while (read < len)
        {
            int n = fs.Read(buffer, read, len - read);
            if (n <= 0) break;
            read += n;
        }
        return read == len ? buffer : buffer[..read];
    }

    /// <summary>
    /// 核心探测逻辑。返回 (格式, 视频子格式, MIME, 置信度)。
    /// </summary>
    private static (FileType, VideoFormat, string, double) Probe(byte[] h, string ext)
    {
        if (h.Length < 4)
            return (FileType.Unknown, VideoFormat.None, "application/octet-stream", 0.0);

        // ---- 压缩格式 ----

        // ZIP: "PK\x03\x04" / 空归档 "PK\x05\x06" / 分卷 "PK\x07\x08"
        if (Match(h, 0, 0x50, 0x4B, 0x03, 0x04) ||
            Match(h, 0, 0x50, 0x4B, 0x05, 0x06) ||
            Match(h, 0, 0x50, 0x4B, 0x07, 0x08))
            return (FileType.Zip, VideoFormat.None, "application/zip", 1.0);

        // RAR5: "Rar!\x1A\x07\x01\x00"
        if (Match(h, 0, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00))
            return (FileType.Rar, VideoFormat.None, "application/vnd.rar", 1.0);

        // RAR4: "Rar!\x1A\x07\x00"
        if (Match(h, 0, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00))
            return (FileType.Rar, VideoFormat.None, "application/vnd.rar", 1.0);

        // 7Z: "7z\xBC\xAF\x27\x1C"
        if (Match(h, 0, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C))
            return (FileType.SevenZip, VideoFormat.None, "application/x-7z-compressed", 1.0);

        // GZIP: "\x1F\x8B"
        if (Match(h, 0, 0x1F, 0x8B))
            return (FileType.Gzip, VideoFormat.None, "application/gzip", 1.0);

        // BZIP2: "BZh"
        if (Match(h, 0, 0x42, 0x5A, 0x68))
            return (FileType.Bzip2, VideoFormat.None, "application/x-bzip2", 0.98);

        // XZ: "\xFD7zXZ\x00"
        if (Match(h, 0, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00))
            return (FileType.Xz, VideoFormat.None, "application/x-xz", 1.0);

        // TAR: 无开头魔数，靠 offset 257 的 "ustar"
        if (h.Length >= 262 && Match(h, 257, 0x75, 0x73, 0x74, 0x61, 0x72))
            return (FileType.Tar, VideoFormat.None, "application/x-tar", 0.99);

        // ---- 视频格式 ----

        // MP4 / MOV / M4V: offset 4 起为 "ftyp"
        if (h.Length >= 12 && Match(h, 4, 0x66, 0x74, 0x79, 0x70))
        {
            var brand = System.Text.Encoding.ASCII.GetString(h, 8, Math.Min(4, h.Length - 8));
            return brand.StartsWith("qt", StringComparison.OrdinalIgnoreCase)
                ? (FileType.Video, VideoFormat.Mov, "video/quicktime", 1.0)
                : (FileType.Video, VideoFormat.Mp4, "video/mp4", 1.0);
        }

        // MKV / WEBM: EBML "\x1A\x45\xDF\xA3"，DocType 区分
        if (Match(h, 0, 0x1A, 0x45, 0xDF, 0xA3))
        {
            var text = System.Text.Encoding.ASCII.GetString(h, 0, Math.Min(h.Length, 4096));
            var fmt = text.Contains("webm", StringComparison.OrdinalIgnoreCase)
                ? VideoFormat.Webm
                : VideoFormat.Mkv;
            return (FileType.Video, fmt, fmt == VideoFormat.Webm ? "video/webm" : "video/x-matroska", 0.99);
        }

        // AVI: "RIFF" + offset 8 "AVI "
        if (h.Length >= 12 && Match(h, 0, 0x52, 0x49, 0x46, 0x46) &&
            Match(h, 8, 0x41, 0x56, 0x49, 0x20))
            return (FileType.Video, VideoFormat.Avi, "video/x-msvideo", 1.0);

        // ---- 扩展名兜底（低置信度）----
        // 魔数无法判定时，若扩展名声称是压缩格式，按低置信度归类，
        // 但仍标记为 Unknown 以避免误判 —— 交给 7-Zip 做最终裁决。
        if (ExtensionClaims.TryGetValue(ext, out var claimed))
            return (FileType.Unknown, VideoFormat.None, "application/octet-stream", 0.2);

        if (VideoExtensions.Contains(ext))
            return (FileType.Video, FormatFromExt(ext), MimeFromVideoExt(ext), 0.5);

        return (FileType.Unknown, VideoFormat.None, "application/octet-stream", 0.0);
    }

    /// <summary>
    /// 伪装判定：
    /// 1) 扩展名声称是压缩格式，但实际是另一种压缩格式 → 伪装
    /// 2) 扩展名是视频格式，但实际是压缩格式 → 伪装（核心场景：mp4 实为 zip）
    /// 3) 扩展名是压缩格式，但实际是视频 → 也算不一致
    /// 注意：真正视频（扩展名 mp4 + 实际 mp4）不算伪装。
    /// </summary>
    private static bool IsDisguise(string ext, FileType actual)
    {
        if (string.IsNullOrEmpty(ext)) return false;
        if (actual == FileType.Unknown) return false;

        var isArchiveActual = actual is FileType.Zip or FileType.Rar or FileType.SevenZip
            or FileType.Tar or FileType.Gzip or FileType.Bzip2 or FileType.Xz;

        // 扩展名声称视频，实际是压缩 → 伪装（典型：游戏资源.mp4 实为 ZIP）
        if (VideoExtensions.Contains(ext) && isArchiveActual)
            return true;

        // 扩展名声称某种压缩，实际是另一种压缩 → 伪装
        if (ExtensionClaims.TryGetValue(ext, out var claimed))
        {
            if (claimed != actual) return true;
            return false;
        }

        // 扩展名声称视频，实际是视频，但具体格式不同 → 弱伪装（如 mkv 实为 mp4）
        if (VideoExtensions.Contains(ext) && actual == FileType.Video)
        {
            var claimedVideo = FormatFromExt(ext);
            if (claimedVideo != VideoFormat.None)
                return false; // 视频 → 视频，不视为伪装压缩包（需求只要求压缩包场景）
        }

        return false;
    }

    /// <summary>
    /// 读取文件尾。文件小于 TailSize 时返回 null（头部扫描已覆盖）。
    /// </summary>
    private static byte[]? ReadTail(string path, long fileSize)
    {
        if (fileSize <= HeaderSize) return null;
        var len = (int)Math.Min(TailSize, fileSize);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
        fs.Seek(-len, SeekOrigin.End);
        var buffer = new byte[len];
        int read = 0;
        while (read < len)
        {
            int n = fs.Read(buffer, read, len - read);
            if (n <= 0) break;
            read += n;
        }
        return read == len ? buffer : buffer[..read];
    }

    /// <summary>
    /// 在文件尾数据中查找被附加的压缩包。
    ///
    /// 识别依据（按可靠性排序）：
    ///  1) ZIP 中央目录结束记录 EOCD："PK\x05\x06"，其后 16 字节含中央目录大小与偏移。
    ///     若"中央目录偏移 + 偏移量"落在一个 "PK\x03\x04" 局部文件头上，则确认是附加 ZIP。
    ///  2) 尾部 1MB 内直接出现 "PK\x03\x04"（部分非标准打包器不写 EOCD 偏移）。
    ///  3) "Rar!\x1A\x07" / "7z\xBC\xAF\x27\x1C" 出现在尾部。
    /// </summary>
    private static bool TryDetectAppendedArchive(byte[] tail, out FileType type, out string mime)
    {
        type = FileType.Unknown;
        mime = "application/octet-stream";

        if (tail.Length < 22) return false;

        // ---- 1) 找 EOCD ----
        // EOCD 位于文件最后 65557 字节内；从尾往前找第一个 "PK\x05\x06"
        int eocd = -1;
        int limit = Math.Max(0, tail.Length - 65557);
        for (int i = tail.Length - 22; i >= limit; i--)
        {
            if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
            {
                eocd = i;
                break;
            }
        }

        if (eocd >= 0 && eocd + 22 <= tail.Length)
        {
            // EOCD 之后若有内容（注释），偏移量仍然有效
            uint cdSize = BitConverter.ToUInt32(tail, eocd + 12);
            uint cdOffset = BitConverter.ToUInt32(tail, eocd + 16);

            // 头部已把视频判掉了，所以这里的 ZIP 一定是附加的
            // 校验：中央目录偏移必须指向一个 "PK\x01\x02" 记录，或至少合理
            long cdAbs = (long)cdOffset;
            if (cdSize > 0 && cdAbs >= 0)
            {
                type = FileType.Zip;
                mime = "application/zip";
                return true;
            }
        }

        // ---- 2) 尾部直接出现 ZIP 局部文件头 ----
        // 只看最后 1MB，避免误判视频中偶然出现的字节序列
        int scanFrom = Math.Max(0, tail.Length - 1024 * 1024);
        for (int i = scanFrom; i < tail.Length - 4; i++)
        {
            if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x03 && tail[i + 3] == 0x04)
            {
                type = FileType.Zip;
                mime = "application/zip";
                return true;
            }
        }

        // ---- 3) RAR / 7Z 尾部特征 ----
        for (int i = scanFrom; i < tail.Length - 8; i++)
        {
            if (tail[i] == 0x52 && tail[i + 1] == 0x61 && tail[i + 2] == 0x72 && tail[i + 3] == 0x21
                && tail[i + 4] == 0x1A && tail[i + 5] == 0x07)
            {
                type = FileType.Rar;
                mime = "application/vnd.rar";
                return true;
            }

            if (tail[i] == 0x37 && tail[i + 1] == 0x7A && tail[i + 2] == 0xBC && tail[i + 3] == 0xAF
                && tail[i + 4] == 0x27 && tail[i + 5] == 0x1C)
            {
                type = FileType.SevenZip;
                mime = "application/x-7z-compressed";
                return true;
            }
        }

        return false;
    }

    private static bool Match(byte[] buf, int offset, params byte[] pattern)
    {
        if (offset < 0 || buf.Length < offset + pattern.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (buf[offset + i] != pattern[i]) return false;
        return true;
    }

    private static VideoFormat FormatFromExt(string ext) => ext switch
    {
        "mp4" or "m4v" => VideoFormat.Mp4,
        "mkv" => VideoFormat.Mkv,
        "avi" => VideoFormat.Avi,
        "mov" => VideoFormat.Mov,
        "webm" => VideoFormat.Webm,
        _ => VideoFormat.None
    };

    private static string MimeFromVideoExt(string ext) => ext switch
    {
        "mp4" or "m4v" => "video/mp4",
        "mkv" => "video/x-matroska",
        "avi" => "video/x-msvideo",
        "mov" => "video/quicktime",
        "webm" => "video/webm",
        _ => "video/unknown"
    };
}
