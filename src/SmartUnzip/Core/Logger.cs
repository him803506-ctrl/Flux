using System.IO;
using System.Text;

namespace SmartUnzip.Core;

/// <summary>
/// 简单日志。铁律：绝不记录密码。
/// 所有对外写入的文本都经过 <see cref="MaskSecrets"/> 过滤。
/// </summary>
public sealed class Logger
{
    private readonly object _gate = new();
    private readonly string _logPath;
    private readonly bool _enabled;

    public event Action<string>? LineWritten;

    public Logger(bool enabled = true)
    {
        _enabled = enabled;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            // 目录名保持 "SmartUnzip" 不改：改了就与旧版日志断档。
            "SmartUnzip", "logs");
        try
        {
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, $"smartunzip-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            _logPath = Path.Combine(Path.GetTempPath(), "smartunzip.log");
        }
    }

    public string LogPath => _logPath;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
    public void Debug(string message) => Write("DEBUG", message);

    private void Write(string level, string message)
    {
        if (!_enabled) return;
        var safe = MaskSecrets(message);
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {safe}";

        lock (_gate)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志写失败绝不影响主流程
            }
        }
        try { LineWritten?.Invoke(line); } catch { }
    }

    /// <summary>
    /// 过滤疑似密码内容。规则：
    ///  - 独立 token 形式的 "-pXXXX" → "-p***"
    ///    （必须前面是空白或引号，避免误伤路径中的 "-probe" 之类）
    ///  - 包含"密码"且带冒号/等号的行 → 值部分打码
    /// </summary>
    public static string MaskSecrets(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // "-p<password>" 作为独立参数（前面是行首/空白/引号）→ -p***
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(?<=^|[\s""'])-p(?!\*)[^\s""]+", "-p***");

        // "密码：xxxx" / "password=xxxx" → 打码
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(密码|password|尝试密码)\s*[:：=]\s*\S+",
            m => m.Value[..m.Value.IndexOfAny([':', '：', '='])] + " ***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return text;
    }
}
