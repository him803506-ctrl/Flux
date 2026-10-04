using System.IO;

namespace SmartUnzip.Core;

/// <summary>
/// 密码匹配结果。
/// </summary>
public sealed class PasswordMatchResult
{
    public bool Found { get; init; }
    public string? Password { get; init; }
    public string? MatchedEntryId { get; init; }
    public string? MatchedEntryName { get; init; }
    public bool AutoMatched { get; init; }
    public int TriedCount { get; init; }
}

/// <summary>
/// 密码自动匹配器。
///
/// 严格限制：仅尝试「用户自己保存过的密码」。不包含暴力破解、字典攻击、
/// 密码生成或任何绕过加密的行为。
/// </summary>
public sealed class PasswordMatcher
{
    private readonly PasswordVault _vault;
    private readonly IArchiveEngine _engine;
    private readonly Logger _log;

    /// <summary>
    /// 本次任务期间记住的密码（程序关闭即清除）。
    /// 键为规范化后的压缩包路径。
    /// </summary>
    private readonly Dictionary<string, string> _sessionPasswords = new(StringComparer.OrdinalIgnoreCase);

    public PasswordMatcher(PasswordVault vault, IArchiveEngine engine, Logger log)
    {
        _vault = vault;
        _engine = engine;
        _log = log;
    }

    /// <summary>本次任务中记住的密码数量。</summary>
    public int SessionPasswordCount { get { lock (_sessionPasswords) return _sessionPasswords.Count; } }

    public void RememberForSession(string archivePath, string password)
    {
        lock (_sessionPasswords)
            _sessionPasswords[Path.GetFullPath(archivePath)] = password;
        _log.Info("已在本次任务中记住密码（不落盘）");
    }

    public string? GetSessionPassword(string archivePath)
    {
        lock (_sessionPasswords)
            return _sessionPasswords.TryGetValue(Path.GetFullPath(archivePath), out var p) ? p : null;
    }

    public void ClearSession()
    {
        lock (_sessionPasswords) _sessionPasswords.Clear();
        _log.Info("本次任务记住的密码已清除");
    }

    /// <summary>
    /// 自动匹配密码。顺序：
    ///  1. 本次任务记住的密码
    ///  2. 密码库（按配置的策略排序：最近使用优先 / 库顺序）
    /// 找到一个可用的即返回。
    /// </summary>
    public async Task<PasswordMatchResult> MatchAsync(string archivePath, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        int tried = 0;

        // 1. 会话密码
        var session = GetSessionPassword(archivePath);
        if (!string.IsNullOrEmpty(session))
        {
            ct.ThrowIfCancellationRequested();
            tried++;
            progress?.Report("尝试本次任务记住的密码…");
            var (ok, wrong) = await _engine.TestAsync(archivePath, session, ct).ConfigureAwait(false);
            if (ok)
            {
                _log.Info("本次任务记住的密码匹配成功");
                return new PasswordMatchResult
                {
                    Found = true,
                    Password = session,
                    AutoMatched = true,
                    TriedCount = tried
                };
            }
        }

        // 2. 密码库
        var candidates = _vault.GetCandidates();
        if (candidates.Count == 0)
        {
            _log.Info("密码库为空，无法自动匹配");
            return new PasswordMatchResult { Found = false, TriedCount = tried };
        }

        _log.Info($"开始匹配密码库，共 {candidates.Count} 个候选");

        // 去重：同名同密码只试一次
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Password)) continue;
            if (!seen.Add(entry.Password)) continue;

            tried++;
            // 注意：进度消息中不包含密码本身
            progress?.Report($"正在尝试密码库（{tried}/{candidates.Count}）…");

            var (ok, wrong) = await _engine.TestAsync(archivePath, entry.Password, ct).ConfigureAwait(false);
            if (ok)
            {
                _vault.MarkUsed(entry.Id);
                _log.Info($"密码匹配成功（条目：{entry.Name}）");
                progress?.Report("密码匹配成功");
                return new PasswordMatchResult
                {
                    Found = true,
                    Password = entry.Password,
                    MatchedEntryId = entry.Id,
                    MatchedEntryName = entry.Name,
                    AutoMatched = true,
                    TriedCount = tried
                };
            }
        }

        _log.Info($"密码库匹配失败（已尝试 {tried} 个）");
        return new PasswordMatchResult { Found = false, TriedCount = tried };
    }

    /// <summary>
    /// 仅对单个密码做校验（用于用户手动输入后验证）。
    /// </summary>
    public async Task<bool> VerifyAsync(string archivePath, string password, CancellationToken ct = default)
    {
        var (ok, _) = await _engine.TestAsync(archivePath, password, ct).ConfigureAwait(false);
        return ok;
    }
}
