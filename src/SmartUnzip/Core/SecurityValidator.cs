using System.IO;

namespace SmartUnzip.Core;

/// <summary>
/// 解压安全检查。防止 Zip Slip / Path Traversal：
/// 压缩包内形如 "../../evil.exe" 的条目绝不能写到目标目录之外。
/// </summary>
public static class SecurityValidator
{
    /// <summary>单个路径最大允许长度（Windows 传统上限）。</summary>
    private const int MaxPathLength = 259;

    /// <summary>
    /// 校验单个条目相对路径是否安全地落在 targetDir 内。
    /// 返回规范化后的完整路径；不安全则抛出 <see cref="SecurityException"/>。
    /// </summary>
    public static string ValidateEntryPath(string targetDir, string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
            throw new SecurityException("压缩包中存在空路径条目。");

        // 归一化分隔符
        var normalized = entryPath.Replace('/', Path.DirectorySeparatorChar)
                                  .Replace('\\', Path.DirectorySeparatorChar);

        // 拒绝绝对路径与盘符路径
        if (Path.IsPathRooted(normalized) || normalized.StartsWith(@"\\", StringComparison.Ordinal))
            throw new SecurityException($"检测到绝对路径条目，已阻止解压：{entryPath}");

        // 拒绝 UNC 与卷影路径
        if (normalized.Contains(@":\", StringComparison.Ordinal))
            throw new SecurityException($"检测到盘符路径条目，已阻止解压：{entryPath}");

        var targetFull = Path.GetFullPath(targetDir);
        if (!targetFull.EndsWith(Path.DirectorySeparatorChar))
            targetFull += Path.DirectorySeparatorChar;

        var combined = Path.GetFullPath(Path.Combine(targetFull, normalized));

        // 核心判据：规范化后的路径必须以目标目录为前缀
        if (!combined.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException($"检测到路径穿越（Zip Slip），已阻止解压：{entryPath}");

        if (combined.Length > MaxPathLength)
            throw new SecurityException($"条目路径过长（{combined.Length} 字符），可能无法解压：{entryPath}");

        return combined;
    }

    /// <summary>
    /// 批量校验。返回 (是否安全, 第一个问题条目, 错误消息)。
    /// 不抛异常，供 UI 做预检提示。
    /// </summary>
    public static (bool safe, string? badEntry, string? message) ValidateAll(
        string targetDir, IEnumerable<string> entries)
    {
        foreach (var e in entries)
        {
            try
            {
                ValidateEntryPath(targetDir, e);
            }
            catch (SecurityException ex)
            {
                return (false, e, ex.Message);
            }
            catch (Exception ex)
            {
                return (false, e, $"路径校验失败：{ex.Message}");
            }
        }
        return (true, null, null);
    }

    /// <summary>
    /// 解压后扫描输出目录，若发现符号链接（可能指向目录外）则报告。
    /// 返回可疑路径列表。
    /// </summary>
    public static IReadOnlyList<string> FindEscapingSymlinks(string targetDir)
    {
        var result = new List<string>();
        try
        {
            var targetFull = Path.GetFullPath(targetDir);
            foreach (var entry in Directory.EnumerateFileSystemEntries(targetFull, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(entry);
                    if (info.LinkTarget is { } target)
                    {
                        // 链接目标为绝对路径且不在目标目录内 → 可疑
                        if (Path.IsPathRooted(target))
                        {
                            var resolved = Path.GetFullPath(target);
                            if (!resolved.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
                                result.Add(entry);
                        }
                        else
                        {
                            var resolved = Path.GetFullPath(Path.Combine(info.DirectoryName ?? targetFull, target));
                            if (!resolved.StartsWith(targetFull, StringComparison.OrdinalIgnoreCase))
                                result.Add(entry);
                        }
                    }
                }
                catch
                {
                    // 单个条目检查失败忽略
                }
            }
        }
        catch
        {
            // 目录不可枚举则跳过
        }
        return result;
    }

    /// <summary>
    /// 生成输出目录名。若已存在，按配置决定复用 / 新建。
    /// </summary>
    public static string ResolveOutputDirectory(string sourcePath, OutputCollisionPolicy policy)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? Environment.CurrentDirectory;
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(name)) name = "Flux_输出";

        var baseOut = Path.Combine(dir, name);
        if (!Directory.Exists(baseOut)) return baseOut;

        return policy switch
        {
            OutputCollisionPolicy.Reuse => baseOut,
            OutputCollisionPolicy.NewFolder => MakeUnique(baseOut),
            _ => baseOut
        };
    }

    private static string MakeUnique(string basePath)
    {
        for (int i = 2; i < 10000; i++)
        {
            var candidate = $"{basePath} ({i})";
            if (!Directory.Exists(candidate)) return candidate;
        }
        return $"{basePath} ({Guid.NewGuid():N})";
    }
}

public enum OutputCollisionPolicy
{
    /// <summary>使用已有目录（默认推荐新建）</summary>
    Reuse,
    /// <summary>创建新目录（名称后加序号）</summary>
    NewFolder,
    /// <summary>取消操作</summary>
    Cancel
}

public sealed class SecurityException(string message) : Exception(message);
