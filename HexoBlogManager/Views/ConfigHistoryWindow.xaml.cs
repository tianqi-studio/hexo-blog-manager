using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HexoBlogManager.Services;

namespace HexoBlogManager.Views;

/// <summary>
/// 配置历史备份窗口。
///
/// 保存配置时工具会自动留 .bak，但之前没有还原入口——改坏一个缩进导致
/// hexo generate 失败时，用户只能自己去文件夹里手动改名复制。
/// 这个窗口把那份备份变成能点的一步操作。
/// </summary>
public partial class ConfigHistoryWindow : Window
{
    /// <summary>可还原的目标文件。</summary>
    private sealed record Target(string Label, string Path);

    private readonly ConfigService _service;
    private readonly List<Target> _targets = new();

    /// <summary>还原成功后置为 true，调用方据此重新加载配置。</summary>
    public bool Restored { get; private set; }

    public ConfigHistoryWindow(ConfigService service, string mainPath, string? themePath)
    {
        InitializeComponent();

        _service = service;

        if (File.Exists(mainPath))
            _targets.Add(new Target("_config.yml（站点基础）", mainPath));

        if (!string.IsNullOrWhiteSpace(themePath) && File.Exists(themePath))
        {
            string name = Path.GetFileName(themePath);
            _targets.Add(new Target($"{name}（主题配置）", themePath!));
        }

        CmbTarget.ItemsSource = _targets;

        if (_targets.Count > 0)
            CmbTarget.SelectedIndex = 0;
        else
            TxtSubtitle.Text = "没有找到可用的配置文件";
    }

    private void CmbTarget_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadHistory();

    private void LoadHistory()
    {
        if (CmbTarget.SelectedItem is not Target target)
        {
            HistoryList.ItemsSource = null;
            EmptyHint.Visibility = Visibility.Visible;
            HistoryList.Visibility = Visibility.Collapsed;
            BtnRestore.IsEnabled = false;
            return;
        }

        TxtSubtitle.Text = target.Path;

        var items = _service.ListBackups(target.Path);

        HistoryList.ItemsSource = items;

        bool empty = items.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        BtnRestore.IsEnabled = !empty;

        if (!empty)
            HistoryList.SelectedIndex = 0;
    }

    private void BtnRestore_Click(object sender, RoutedEventArgs e) => RestoreSelected();

    private void HistoryList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => RestoreSelected();

    private void RestoreSelected()
    {
        if (CmbTarget.SelectedItem is not Target target)
            return;

        if (HistoryList.SelectedItem is not ConfigBackupEntry entry)
            return;

        var result = MessageBox.Show(this,
            $"确定要把 {Path.GetFileName(target.Path)} 还原到 {entry.TimeDisplay} 的版本吗？\n\n" +
            "当前文件会先被备份一份，还原之后需要重新生成站点才会生效。",
            "还原配置备份", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            return;

        if (!_service.RestoreBackup(entry.FilePath, target.Path, out string error))
        {
            MessageBox.Show(this, "还原失败：" + error, "还原配置备份",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LogService.Info($"已还原配置备份：{target.Path} ← {Path.GetFileName(entry.FilePath)}");

        Restored = true;
        DialogResult = true;
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = CmbTarget.SelectedItem is Target target
                ? Path.GetDirectoryName(target.Path) ?? string.Empty
                : string.Empty;

            if (dir.Length == 0 || !Directory.Exists(dir))
            {
                MessageBox.Show(this, "找不到配置文件所在目录。", "打开失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开目录：" + ex.Message, "打开失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
