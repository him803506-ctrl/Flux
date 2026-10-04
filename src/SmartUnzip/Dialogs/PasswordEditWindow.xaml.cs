using System.Windows;
using SmartUnzip.Core;

namespace SmartUnzip.Dialogs;

public partial class PasswordEditWindow : Window
{
    private readonly PasswordEntry? _existing;

    public PasswordEditWindow(PasswordEntry? existing)
    {
        InitializeComponent();
        _existing = existing;

        if (existing is not null)
        {
            HeaderText.Text = "编辑密码";
            NameBox.Text = existing.Name;
            PasswordMasked.Password = existing.Password;
            PasswordPlain.Text = existing.Password;
            NoteBox.Text = existing.Note;
        }
    }

    private string CurrentPassword =>
        PasswordPlain.Visibility == Visibility.Visible ? PasswordPlain.Text : PasswordMasked.Password;

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordPlain.Visibility == Visibility.Visible)
        {
            PasswordMasked.Password = PasswordPlain.Text;
            PasswordMasked.Visibility = Visibility.Visible;
            PasswordPlain.Visibility = Visibility.Collapsed;
            PasswordMasked.Focus();
        }
        else
        {
            PasswordPlain.Text = PasswordMasked.Password;
            PasswordPlain.Visibility = Visibility.Visible;
            PasswordMasked.Visibility = Visibility.Collapsed;
            PasswordPlain.Focus();
            PasswordPlain.CaretIndex = PasswordPlain.Text.Length;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var pwd = CurrentPassword;
        if (string.IsNullOrEmpty(pwd))
        {
            MessageBox.Show("请输入密码。", "Flux", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "未命名密码";

        if (_existing is null)
        {
            App.Vault.Add(new PasswordEntry
            {
                Name = name,
                Password = pwd,
                Note = NoteBox.Text.Trim()
            });
        }
        else
        {
            _existing.Name = name;
            _existing.Password = pwd;
            _existing.Note = NoteBox.Text.Trim();
            App.Vault.Update(_existing);
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
