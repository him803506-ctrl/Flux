using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SmartUnzip.Core;

namespace SmartUnzip.Dialogs;

/// <summary>密码库列表项视图模型。</summary>
public sealed class VaultItem
{
    private readonly PasswordEntry _entry;
    private bool _revealed;

    public VaultItem(PasswordEntry entry) => _entry = entry;

    public PasswordEntry Entry => _entry;

    public string Name => string.IsNullOrWhiteSpace(_entry.Name) ? "（未命名）" : _entry.Name;

    public string MaskedPassword => _revealed ? _entry.Password : new string('●', Math.Min(12, Math.Max(6, _entry.Password.Length)));

    public string MetaText
    {
        get
        {
            var parts = new List<string> { $"创建于 {_entry.CreatedAt:yyyy-MM-dd}" };
            if (_entry.LastUsedAt is { } lu) parts.Add($"最后使用 {lu:yyyy-MM-dd HH:mm}");
            if (_entry.UseCount > 0) parts.Add($"使用 {_entry.UseCount} 次");
            if (!string.IsNullOrWhiteSpace(_entry.Note)) parts.Add(_entry.Note);
            return string.Join("  ·  ", parts);
        }
    }

    public void ToggleReveal() => _revealed = !_revealed;
}

public partial class VaultWindow : Window
{
    private readonly ObservableCollection<VaultItem> _items = [];

    public VaultWindow()
    {
        InitializeComponent();
        EntryList.ItemsSource = _items;
        Reload();
    }

    private void Reload(string? keyword = null)
    {
        _items.Clear();
        var source = string.IsNullOrWhiteSpace(keyword)
            ? App.Vault.Entries
            : App.Vault.Search(keyword);

        foreach (var e in source) _items.Add(new VaultItem(e));

        var has = _items.Count > 0;
        EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ListScroll.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        VaultSubtitle.Text = $"共 {App.Vault.Count} 个密码"
            + (string.IsNullOrWhiteSpace(keyword) ? "" : $"，匹配 {_items.Count} 个")
            + "  ·  DPAPI 加密存储";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text;
        if (text == SearchBox.Tag as string) return;
        Reload(text);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PasswordEditWindow(null) { Owner = this };
        if (dlg.ShowDialog() == true) Reload(SearchBox.Text);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultItem item }) return;
        var dlg = new PasswordEditWindow(item.Entry) { Owner = this };
        if (dlg.ShowDialog() == true) Reload(SearchBox.Text);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultItem item }) return;
        var r = MessageBox.Show($"确定删除密码「{item.Name}」？此操作不可撤销。",
            "Flux", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r == MessageBoxResult.OK)
        {
            App.Vault.Remove(item.Entry.Id);
            Reload(SearchBox.Text);
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultItem item }) return;
        item.ToggleReveal();
        Reload(SearchBox.Text);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VaultItem item }) return;
        try
        {
            Clipboard.SetText(item.Entry.Password);
            MessageBox.Show("密码已复制到剪贴板。", "Flux",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败：{ex.Message}", "Flux",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
