using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SmartUnzip.Core;

namespace SmartUnzip;

/// <summary>
/// 任务卡片视图模型。把 Core 的 ExtractionTask 适配到 UI。
/// </summary>
public sealed class TaskItem : INotifyPropertyChanged
{
    private readonly ExtractionTask _task;

    public TaskItem(ExtractionTask task) => _task = task;

    public ExtractionTask Model => _task;

    public string FileName => _task.FileName;

    public string Icon => _task.Info?.ActualType switch
    {
        Core.FileType.Video => "\U0001F3AC",
        Core.FileType.Zip or Core.FileType.Rar or Core.FileType.SevenZip
            or Core.FileType.Tar or Core.FileType.Gzip or Core.FileType.Bzip2 or Core.FileType.Xz => "\U0001F4E6",
        _ => "\U0001F4C4"
    };

    public string ExtensionDisplay => _task.Info?.ExtensionDisplay ?? "-";

    public string ActualTypeDisplay => _task.Info?.ActualTypeDisplay ?? "-";

    public string SizeDisplay => _task.Info?.SizeDisplay ?? "-";

    public string StateText => _task.StateText;

    public Brush StateColor => _task.State switch
    {
        TaskState.Completed => new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x0F)),
        TaskState.Failed => new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
        TaskState.Cancelled => new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
        TaskState.WaitingPassword => new SolidColorBrush(Color.FromRgb(0x9D, 0x5D, 0x00)),
        TaskState.Extracting or TaskState.Detecting => new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)),
        TaskState.Skipped => new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61)),
        _ => new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61))
    };

    public string DetailText
    {
        get
        {
            var t = _task;
            if (t.ErrorMessage is { Length: > 0 }) return t.ErrorMessage;

            if (t.Info is { } info)
            {
                if (info.IsEmbeddedArchive)
                    return $"检测到尾部附加{info.ActualTypeDisplay}压缩包（伪装成视频），可直接一键解压。";
                if (info.IsDisguised)
                    return $"检测到扩展名伪装：{info.ExtensionDisplay} → {info.ActualTypeDisplay}，可直接一键解压。";
                if (info.IsEncrypted) return "压缩包已加密，将自动匹配密码库。";
                if (info.IsVideo) return "文件真实格式为视频，不做任何处理。";
                if (info.IsArchive) return "压缩包格式正常，可解压。";
            }
            return string.Empty;
        }
    }

    public Visibility DetailVisibility =>
        string.IsNullOrEmpty(DetailText) ? Visibility.Collapsed : Visibility.Visible;

    public bool IsRunning => _task.State is TaskState.Extracting or TaskState.Detecting
        or TaskState.WaitingPassword;

    public Visibility ProgressVisibility =>
        _task.State is TaskState.Extracting ? Visibility.Visible : Visibility.Collapsed;

    public double Progress => _task.Progress;

    public string ProgressText
    {
        get
        {
            var t = _task;
            if (t.State != TaskState.Extracting) return string.Empty;
            var pct = t.Progress.ToString("0");
            if (t.TotalBytes > 0)
                return $"{pct}%   {FileTypeInfo.FormatSize(t.ProcessedBytes)} / {FileTypeInfo.FormatSize(t.TotalBytes)}";
            return $"{pct}%";
        }
    }

    public Visibility OpenFolderVisibility =>
        _task.State == TaskState.Completed && !string.IsNullOrEmpty(_task.OutputDirectory)
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CancelVisibility =>
        IsRunning ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExtractVisibility =>
        _task.State is TaskState.Pending or TaskState.Failed
            && _task.Info is { } i && i.IsArchive
            ? Visibility.Visible : Visibility.Collapsed;

    public string? OutputDirectory => _task.OutputDirectory;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => OnPropertyChanged(string.Empty);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
