using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartUnzip.Core;

/// <summary>
/// 密码库条目。
/// </summary>
public sealed class PasswordEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>名称（如"游戏资源密码"）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>密码明文。仅在内存中存在；落盘时单独 DPAPI 加密。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>备注。</summary>
    public string Note { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>最后使用时间（用于"最近使用优先"策略）。</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>成功使用次数。</summary>
    public int UseCount { get; set; }
}

/// <summary>
/// 落盘模型。注意：Password 字段在此结构中出现，但整个结构序列化后会经 DPAPI 加密。
/// </summary>
internal sealed class VaultFile
{
    public int Version { get; set; } = 1;

    public List<PasswordEntry> Entries { get; set; } = [];

    /// <summary>密码匹配顺序策略："recent" 或 "order"。</summary>
    public string MatchOrder { get; set; } = "recent";
}

/// <summary>
/// 密码库。支持任意数量密码；整个密码库用 Windows DPAPI 加密后落盘。
///
/// 安全设计：
///  - 落盘内容是 DPAPI（CurrentUser 范围）加密后的密文，普通文本编辑器打开是乱码。
///  - 密码绝不写入日志。
///  - 用户打开普通配置文件（如 settings.json）看不到任何密码。
/// </summary>
public sealed class PasswordVault
{
    /// <summary>DPAPI 附加熵，防止其他程序直接解密本文件。</summary>
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("SmartUnzip.PasswordVault.v1.2026");

    private readonly object _gate = new();
    private readonly Logger _log;
    private readonly string _vaultPath;
    private List<PasswordEntry> _entries = [];
    private string _matchOrder = "recent";

    public PasswordVault(Logger log)
    {
        _log = log;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartUnzip");
        Directory.CreateDirectory(dir);
        _vaultPath = Path.Combine(dir, "vault.dat");
        Load();
    }

    public string VaultPath => _vaultPath;

    /// <summary>当前所有条目（只读快照）。</summary>
    public IReadOnlyList<PasswordEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>匹配顺序："recent"（最近使用优先）或 "order"（密码库顺序）。</summary>
    public string MatchOrder
    {
        get { lock (_gate) return _matchOrder; }
        set
        {
            lock (_gate) _matchOrder = value == "order" ? "order" : "recent";
            Save();
        }
    }

    /// <summary>按匹配策略返回有序的密码候选列表。</summary>
    public IReadOnlyList<PasswordEntry> GetCandidates()
    {
        lock (_gate)
        {
            if (_matchOrder == "order")
                return _entries.ToList();

            return _entries
                .OrderByDescending(e => e.LastUsedAt ?? DateTime.MinValue)
                .ThenByDescending(e => e.UseCount)
                .ToList();
        }
    }

    public void Add(PasswordEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Password)) return;
        lock (_gate)
        {
            entry.Id = string.IsNullOrEmpty(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id;
            entry.CreatedAt = DateTime.Now;
            _entries.Add(entry);
        }
        Save();
        _log.Info($"密码库新增条目：{entry.Name}");
    }

    public void Update(PasswordEntry entry)
    {
        lock (_gate)
        {
            var idx = _entries.FindIndex(e => e.Id == entry.Id);
            if (idx >= 0)
            {
                _entries[idx].Name = entry.Name;
                _entries[idx].Password = entry.Password;
                _entries[idx].Note = entry.Note;
            }
        }
        Save();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            _entries.RemoveAll(x => x.Id == id);
            if (e is not null) _log.Info($"密码库删除条目：{e.Name}");
        }
        Save();
    }

    /// <summary>标记某条密码被成功使用（更新最近使用时间与计数）。</summary>
    public void MarkUsed(string id)
    {
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e is not null)
            {
                e.LastUsedAt = DateTime.Now;
                e.UseCount++;
            }
        }
        Save();
    }

    /// <summary>搜索名称/备注。</summary>
    public IReadOnlyList<PasswordEntry> Search(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return Entries;
        var k = keyword.Trim();
        lock (_gate)
        {
            return _entries.Where(e =>
                e.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                e.Note.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    /// <summary>按名称查找（用于"是否已存在同名密码"的提示）。</summary>
    public bool ContainsPassword(string password)
    {
        lock (_gate)
            return _entries.Any(e => string.Equals(e.Password, password, StringComparison.Ordinal));
    }

    /// <summary>
    /// 保存到磁盘。JSON 序列化 → DPAPI 加密 → 写文件。
    /// </summary>
    public void Save()
    {
        try
        {
            VaultFile model;
            lock (_gate)
            {
                model = new VaultFile
                {
                    Entries = _entries.ToList(),
                    MatchOrder = _matchOrder
                };
            }

            var json = JsonSerializer.SerializeToUtf8Bytes(model, JsonOptions);
            var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_vaultPath, encrypted);
        }
        catch (Exception ex)
        {
            _log.Error($"密码库保存失败：{ex.Message}");
        }
    }

    /// <summary>从磁盘加载。文件损坏时降级为空库并保留原文件备份。</summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(_vaultPath)) return;
            var encrypted = File.ReadAllBytes(_vaultPath);
            if (encrypted.Length == 0) return;

            var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var model = JsonSerializer.Deserialize<VaultFile>(json, JsonOptions);
            if (model is null) return;

            lock (_gate)
            {
                _entries = model.Entries ?? [];
                _matchOrder = model.MatchOrder == "order" ? "order" : "recent";
            }
            _log.Info($"密码库已加载：{_entries.Count} 个条目");
        }
        catch (Exception ex)
        {
            _log.Error($"密码库加载失败（可能因系统账户变更导致无法解密）：{ex.Message}");
            try
            {
                var backup = _vaultPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(_vaultPath, backup, overwrite: true);
                _log.Warn($"已备份损坏的密码库到：{backup}");
            }
            catch { }
            _entries = [];
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
