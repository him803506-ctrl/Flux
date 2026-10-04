using System.IO;
using System.Windows;
using Microsoft.Win32;
using SmartUnzip.Core;

namespace SmartUnzip.Core;

/// <summary>
/// Windows 右键菜单集成（HKCU，无需管理员权限）。
/// 在文件与目录的右键菜单中加入「使用 Flux 解压」。
/// 默认关闭，由设置开关控制。
/// </summary>
public static class ContextMenuManager
{
    private const string FileKey = @"Software\Classes\*\shell\Flux";
    private const string DirKey = @"Software\Classes\Directory\shell\Flux";
    private const string MenuText = "使用 Flux 解压";

    /// <summary>当前是否已注册。</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(FileKey);
            return k is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启用右键菜单。</summary>
    public static (bool ok, string message) Enable(string? exePath = null)
    {
        try
        {
            var exe = exePath ?? Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return (false, "无法定位 Flux.exe 路径。");

            foreach (var key in new[] { FileKey, DirKey })
            {
                using var k = Registry.CurrentUser.CreateSubKey(key, writable: true);
                if (k is null) return (false, $"无法创建注册表项：{key}");

                k.SetValue(null, MenuText, RegistryValueKind.String);
                k.SetValue("Icon", $"\"{exe}\",0", RegistryValueKind.String);

                using var cmd = k.CreateSubKey("command", writable: true);
                cmd?.SetValue(null, $"\"{exe}\" --extract \"%1\"", RegistryValueKind.String);
            }

            return (true, "右键菜单已启用。");
        }
        catch (Exception ex)
        {
            return (false, $"启用失败：{ex.Message}");
        }
    }

    /// <summary>禁用右键菜单。</summary>
    public static (bool ok, string message) Disable()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(FileKey, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(DirKey, throwOnMissingSubKey: false);
            return (true, "右键菜单已禁用。");
        }
        catch (Exception ex)
        {
            return (false, $"禁用失败：{ex.Message}");
        }
    }

    /// <summary>启用/禁用开机启动。</summary>
    public static (bool ok, string message) SetStartup(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "Flux";
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(runKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(runKey, writable: true);
            if (k is null) return (false, "无法访问启动项注册表。");

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return (false, "无法定位程序路径。");
                k.SetValue(valueName, $"\"{exe}\"", RegistryValueKind.String);
                return (true, "已启用开机启动。");
            }

            k.DeleteValue(valueName, throwOnMissingValue: false);
            return (true, "已关闭开机启动。");
        }
        catch (Exception ex)
        {
            return (false, $"设置失败：{ex.Message}");
        }
    }

    public static bool IsStartupEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return k?.GetValue("Flux") is not null;
        }
        catch
        {
            return false;
        }
    }
}
