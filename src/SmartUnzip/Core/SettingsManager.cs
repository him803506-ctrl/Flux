using System.IO;
using System.Text.Json;

namespace SmartUnzip.Core;

/// <summary>
/// 应用设置。
/// 铁律：密码绝不写入此文件（密码只存于 DPAPI 加密的 vault.dat）。
/// </summary>
public sealed class AppSettings
{
    /// <summary>输出目录模式："source"（原文件所在目录）或 "custom"。</summary>
    public string OutputMode { get; set; } = "source";

    /// <summary>自定义输出目录（OutputMode = custom 时生效）。</summary>
    public string CustomOutputDirectory { get; set; } = string.Empty;

    /// <summary>解压后保留原始文件。</summary>
    public bool KeepOriginalFiles { get; set; } = true;

    /// <summary>解压完成后打开目标文件夹。</summary>
    public bool OpenFolderAfterExtract { get; set; } = true;

    /// <summary>自动处理拖入的多个文件。</summary>
    public bool AutoProcessMultiple { get; set; } = true;

    /// <summary>自动匹配密码。</summary>
    public bool AutoMatchPassword { get; set; } = true;

    /// <summary>本次任务中记住密码。</summary>
    public bool RememberPasswordForSession { get; set; } = true;

    /// <summary>启用 Windows 右键菜单。</summary>
    public bool EnableContextMenu { get; set; }

    /// <summary>开机启动。</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>输出目录冲突策略："new"（创建新目录，推荐）/ "reuse"（使用已有目录）。</summary>
    public string OutputCollisionPolicy { get; set; } = "new";

    /// <summary>记录日志。</summary>
    public bool EnableLogging { get; set; } = true;
}

/// <summary>
/// 设置管理器（JSON 持久化）。
/// </summary>
public sealed class SettingsManager
{
    private readonly string _path;
    private readonly Logger _log;

    public AppSettings Current { get; private set; } = new();

    public SettingsManager(Logger log)
    {
        _log = log;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            // 目录名保持 "SmartUnzip" 不改：改了就找不到用户已有的设置与密码库。
            "SmartUnzip");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Load();
    }

    public string SettingsPath => _path;

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null) Current = loaded;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"设置加载失败，使用默认值：{ex.Message}");
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, Options);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            _log.Error($"设置保存失败：{ex.Message}");
        }
    }

    /// <summary>解析输出目录。</summary>
    public OutputCollisionPolicy ResolveCollisionPolicy() =>
        Current.OutputCollisionPolicy switch
        {
            "reuse" => OutputCollisionPolicy.Reuse,
            _ => OutputCollisionPolicy.NewFolder
        };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
