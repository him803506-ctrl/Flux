using System.Diagnostics;
using System.IO;
using System.Windows;
using SmartUnzip.Core;

namespace SmartUnzip.Dialogs;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;

    public SettingsWindow()
    {
        InitializeComponent();
        _s = App.Settings.Current;

        RbSource.IsChecked = _s.OutputMode != "custom";
        RbCustom.IsChecked = _s.OutputMode == "custom";
        CustomDirBox.Text = _s.CustomOutputDirectory;
        ChkKeep.IsChecked = _s.KeepOriginalFiles;
        ChkOpenFolder.IsChecked = _s.OpenFolderAfterExtract;
        ChkAutoMulti.IsChecked = _s.AutoProcessMultiple;
        RbNewFolder.IsChecked = _s.OutputCollisionPolicy != "reuse";
        RbReuse.IsChecked = _s.OutputCollisionPolicy == "reuse";

        ChkAutoMatch.IsChecked = _s.AutoMatchPassword;
        ChkRememberSession.IsChecked = _s.RememberPasswordForSession;
        RbRecent.IsChecked = App.Vault.MatchOrder != "order";
        RbOrder.IsChecked = App.Vault.MatchOrder == "order";

        ChkContextMenu.IsChecked = ContextMenuManager.IsRegistered();
        ChkStartup.IsChecked = ContextMenuManager.IsStartupEnabled();
        ChkLogging.IsChecked = _s.EnableLogging;

        UpdateEnginePanel();
        UpdateVaultInfo();
        OutMode_Changed(null, null);
    }

    private void UpdateEnginePanel()
    {
        var e = App.Engine;
        if (e.IsAvailable)
        {
            EngineStatusText.Text = $"状态：✓ 正常    版本：{e.Version}";
            EnginePathText.Text = $"路径：{e.EnginePath}";
        }
        else
        {
            EngineStatusText.Text = "状态：✗ 未检测到 7-Zip";
            EnginePathText.Text = "请从 https://www.7-zip.org/ 安装后点击「重新检测」。";
        }
    }

    private void UpdateVaultInfo()
    {
        VaultInfoText.Text = $"密码库当前有 {App.Vault.Count} 个密码，存储于：{App.Vault.VaultPath}";
    }

    private void OutMode_Changed(object? sender, RoutedEventArgs? e)
    {
        if (CustomDirBox is null) return;
        CustomDirBox.IsEnabled = RbCustom.IsChecked == true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择默认解压目录"
        };
        if (dlg.ShowDialog() == true) CustomDirBox.Text = dlg.FolderName;
    }

    private void Redetect_Click(object sender, RoutedEventArgs e)
    {
        App.Engine.Detect();
        UpdateEnginePanel();
        MessageBox.Show(App.Engine.IsAvailable ? "已重新检测到 7-Zip。" : "仍未检测到 7-Zip。",
            "Flux", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(App.Log.LogPath);
            if (dir is not null && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _s.OutputMode = RbCustom.IsChecked == true ? "custom" : "source";
        _s.CustomOutputDirectory = CustomDirBox.Text.Trim();
        _s.KeepOriginalFiles = ChkKeep.IsChecked == true;
        _s.OpenFolderAfterExtract = ChkOpenFolder.IsChecked == true;
        _s.AutoProcessMultiple = ChkAutoMulti.IsChecked == true;
        _s.OutputCollisionPolicy = RbReuse.IsChecked == true ? "reuse" : "new";

        _s.AutoMatchPassword = ChkAutoMatch.IsChecked == true;
        _s.RememberPasswordForSession = ChkRememberSession.IsChecked == true;
        App.Vault.MatchOrder = RbOrder.IsChecked == true ? "order" : "recent";
        _s.EnableLogging = ChkLogging.IsChecked == true;

        // 右键菜单
        bool wantMenu = ChkContextMenu.IsChecked == true;
        bool hasMenu = ContextMenuManager.IsRegistered();
        if (wantMenu && !hasMenu)
        {
            var (ok, msg) = ContextMenuManager.Enable();
            _s.EnableContextMenu = ok;
            if (!ok) MessageBox.Show(msg, "Flux", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (!wantMenu && hasMenu)
        {
            ContextMenuManager.Disable();
            _s.EnableContextMenu = false;
        }

        // 开机启动
        bool wantStartup = ChkStartup.IsChecked == true;
        bool hasStartup = ContextMenuManager.IsStartupEnabled();
        if (wantStartup != hasStartup)
        {
            var (ok, msg) = ContextMenuManager.SetStartup(wantStartup);
            _s.RunAtStartup = ok && wantStartup;
            if (!ok) MessageBox.Show(msg, "Flux", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        App.Settings.Save();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
