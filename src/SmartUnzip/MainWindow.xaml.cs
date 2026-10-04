using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SmartUnzip.Core;
using SmartUnzip.Dialogs;

namespace SmartUnzip;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TaskItem> _items = [];
    private CancellationTokenSource? _cts;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        TaskList.ItemsSource = _items;

        App.TaskManager.PasswordPromptHandler = ShowPasswordPromptAsync;
        App.TaskManager.TaskUpdated += OnTaskUpdated;

        UpdateEngineStatus();

        // 右键菜单传入的文件：延迟到窗口完全就绪后再处理
        if (App.StartupFiles.Count > 0)
        {
            var startup = App.StartupFiles.ToList();
            Loaded += async (_, _) =>
            {
                // 让 UI 先完成首帧渲染，避免在启动阶段做重活
                await Task.Yield();
                await AddAndProcessAsync(startup);
            };
        }
    }

    private void UpdateEngineStatus()
    {
        var engine = App.Engine;
        if (engine.IsAvailable)
        {
            EngineDot.Fill = new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x0F));
            EngineStatus.Text = $"7-Zip {engine.Version} 已就绪";
        }
        else
        {
            EngineDot.Fill = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
            EngineStatus.Text = "未检测到 7-Zip，无法解压";
        }
    }

    // ==================== 拖拽 ====================

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths is null || paths.Length == 0) return;

        var files = ExpandPaths(paths);
        if (files.Count == 0)
        {
            MessageBox.Show("拖入的内容中没有可处理的文件。", "Flux",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await AddAndProcessAsync(files);
    }

    private static List<string> ExpandPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p))
                {
                    result.Add(p);
                }
                else if (Directory.Exists(p))
                {
                    // 拖入目录时，收集其中的文件（不递归，避免误伤大量文件）
                    result.AddRange(Directory.EnumerateFiles(p));
                }
            }
            catch { }
        }
        return result;
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e) => ChooseFiles();

    private void BtnChoose_Click(object sender, RoutedEventArgs e) => ChooseFiles();

    private void ChooseFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要解压的文件",
            Multiselect = true,
            Filter = "所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            _ = AddAndProcessAsync(dlg.FileNames);
        }
    }

    // ==================== 任务处理 ====================

    private async Task AddAndProcessAsync(IEnumerable<string> files)
    {
        if (_busy) return;

        var list = files.Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0) return;

        var newItems = new List<TaskItem>();
        foreach (var f in list)
        {
            var full = Path.GetFullPath(f);

            // 已在列表中的文件 → 复用原卡片，避免重复卡片
            var existing = _items.FirstOrDefault(
                i => string.Equals(i.Model.SourcePath, full, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // 重置状态，允许重试（例如用户上次取消了、或输错密码后重来）
                existing.Model.State = TaskState.Pending;
                existing.Model.ErrorMessage = null;
                existing.Model.Progress = 0;
                newItems.Add(existing);
                continue;
            }

            var task = new ExtractionTask { SourcePath = full };
            var item = new TaskItem(task);
            _items.Add(item);
            newItems.Add(item);
        }
        RefreshEmptyState();

        if (!App.Engine.IsAvailable)
        {
            MessageBox.Show("未检测到 7-Zip，无法解压。请先安装 7-Zip 后重试。",
                "Flux", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _busy = true;
        BtnExtractAll.IsEnabled = false;
        GlobalStatus.Text = $"正在处理 {newItems.Count} 个文件…";
        _cts = new CancellationTokenSource();

        var progress = new Progress<ExtractionTask>(_ => { });

        try
        {
            // 必须传 UI 已绑定的 ExtractionTask 对象本身（而非路径）。
            // 否则 ProcessAsync 会新建一批任务，TaskUpdated 里的引用匹配失败，
            // 卡片永远不会刷新。
            var tasks = newItems.Select(i => i.Model).ToList();
            await App.TaskManager.ProcessAsync(tasks, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            GlobalStatus.Text = "已取消";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"处理过程中出现错误：{ex.Message}", "Flux",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            BtnExtractAll.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
            UpdateGlobalStatus();

            if (App.Settings.Current.OpenFolderAfterExtract)
            {
                var done = newItems.FirstOrDefault(i => i.Model.State == TaskState.Completed);
                if (done?.OutputDirectory is { } dir) OpenFolder(dir);
            }
        }
    }

    private void OnTaskUpdated(ExtractionTask task)
    {
        // 必须用 BeginInvoke（异步投递），不能用 Invoke（同步等待）。
        // 因为 OnTaskUpdated 由后台线程调用，若同步等待 UI 线程，
        // 而 UI 线程正在 await 该后台任务 → 死锁。
        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess())
        {
            ApplyTaskUpdate(task);
        }
        else
        {
            dispatcher.BeginInvoke(() => ApplyTaskUpdate(task));
        }
    }

    private void ApplyTaskUpdate(ExtractionTask task)
    {
        var item = _items.FirstOrDefault(i => ReferenceEquals(i.Model, task));
        item?.Refresh();
        RefreshEmptyState();
    }

    private void UpdateGlobalStatus()
    {
        if (_items.Count == 0)
        {
            GlobalStatus.Text = string.Empty;
            return;
        }

        int ok = _items.Count(i => i.Model.State == TaskState.Completed);
        int skip = _items.Count(i => i.Model.State == TaskState.Skipped);
        int fail = _items.Count(i => i.Model.State == TaskState.Failed);
        int cancel = _items.Count(i => i.Model.State == TaskState.Cancelled);

        var parts = new List<string>();
        if (ok > 0) parts.Add($"完成 {ok}");
        if (skip > 0) parts.Add($"跳过 {skip}");
        if (fail > 0) parts.Add($"失败 {fail}");
        if (cancel > 0) parts.Add($"取消 {cancel}");
        GlobalStatus.Text = parts.Count > 0 ? string.Join(" · ", parts) : string.Empty;
    }

    private void RefreshEmptyState()
    {
        var has = _items.Count > 0;
        EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        TaskScroll.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        TaskListTitle.Text = has ? $"任务列表（{_items.Count}）" : "任务列表";
        UpdateGlobalStatus();
    }

    // ==================== 卡片按钮 ====================

    private async void ExtractOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TaskItem item }) return;
        if (_busy) return;
        await AddAndProcessAsync([item.Model.SourcePath]);
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        GlobalStatus.Text = "正在取消…";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string dir }) OpenFolder(dir);
    }

    private static void OpenFolder(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
        }
        catch { }
    }

    private async void BtnExtractAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var pending = _items.Where(i => i.Model.Info is { IsArchive: true }
                                        && i.Model.State is TaskState.Pending or TaskState.Failed)
            .Select(i => i.Model.SourcePath).ToList();
        if (pending.Count == 0)
        {
            MessageBox.Show("没有待解压的压缩包。", "Flux",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await AddAndProcessAsync(pending);
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _items.Clear();
        RefreshEmptyState();
    }

    // ==================== 密码对话框 ====================

    private async Task<PasswordPromptResult?> ShowPasswordPromptAsync(ExtractionTask task)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            var dlg = new PasswordPromptWindow(task)
            {
                Owner = this
            };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        });
    }

    // ==================== 其他窗口 ====================

    private void BtnVault_Click(object sender, RoutedEventArgs e)
    {
        var w = new VaultWindow { Owner = this };
        w.ShowDialog();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow { Owner = this };
        var changed = w.ShowDialog();
        if (changed == true)
        {
            UpdateEngineStatus();
        }
    }
}
