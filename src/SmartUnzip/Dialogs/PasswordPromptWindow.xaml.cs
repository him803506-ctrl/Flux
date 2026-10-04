using System.Windows;
using SmartUnzip.Core;

namespace SmartUnzip.Dialogs;

public partial class PasswordPromptWindow : Window
{
    private readonly ExtractionTask _task;

    public PasswordPromptResult? Result { get; private set; }

    public PasswordPromptWindow(ExtractionTask task)
    {
        InitializeComponent();
        _task = task;

        var info = task.Info;
        FileInfoText.Text = info is null
            ? task.FileName
            : $"文件：{info.FileName}\n实际格式：{info.ActualTypeDisplay}";

        MatchResultText.Text = "密码库中的密码均无法解压此文件，请手动输入。";
        ChkRememberSession.IsChecked = App.Settings.Current.RememberPasswordForSession;
        EntryNameBox.Text = System.IO.Path.GetFileNameWithoutExtension(task.FileName) + " 密码";

        Loaded += (_, _) => PasswordMasked.Focus();
    }

    /// <summary>取当前输入的密码（无论在掩码态还是明文态）。</summary>
    private string CurrentPassword =>
        PasswordBox.Visibility == Visibility.Visible ? PasswordBox.Text : PasswordMasked.Password;

    private void ChkShow_Changed(object sender, RoutedEventArgs e) => ToggleVisibility();

    private void BtnToggle_Click(object sender, RoutedEventArgs e)
    {
        ChkShow.IsChecked = !(ChkShow.IsChecked == true);
        ToggleVisibility();
    }

    private void ToggleVisibility()
    {
        bool show = ChkShow.IsChecked == true;
        if (show)
        {
            PasswordBox.Text = PasswordMasked.Password;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordMasked.Visibility = Visibility.Collapsed;
            PasswordBox.Focus();
            PasswordBox.CaretIndex = PasswordBox.Text.Length;
        }
        else
        {
            PasswordMasked.Password = PasswordBox.Text;
            PasswordMasked.Visibility = Visibility.Visible;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordMasked.Focus();
        }
    }

    private void ChkSave_Changed(object sender, RoutedEventArgs e)
    {
        SaveFields.Visibility = ChkSave.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var pwd = CurrentPassword;
        if (string.IsNullOrEmpty(pwd))
        {
            MessageBox.Show("请输入密码。", "Flux", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Result = new PasswordPromptResult
        {
            Password = pwd,
            SaveToVault = ChkSave.IsChecked == true,
            EntryName = EntryNameBox.Text,
            EntryNote = EntryNoteBox.Text,
            RememberForSession = ChkRememberSession.IsChecked == true
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }
}
