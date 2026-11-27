using System.Diagnostics;
using System.IO;
using System.Windows;
using HexoBlogManager.Models;
using HexoBlogManager.Services;

namespace HexoBlogManager.Views;

/// <summary>
/// 文章历史版本窗口。
/// 只做两件事：列出某篇文章的备份、把选中的备份恢复回去。
/// </summary>
public partial class PostHistoryWindow : Window
{
    private readonly PostService _service;
    private readonly PostMeta _meta;

    /// <summary>恢复成功后置为 true，调用方据此重新加载正文。</summary>
    public bool Restored { get; private set; }

    public PostHistoryWindow(PostService service, PostMeta meta)
    {
        InitializeComponent();

        _service = service;
        _meta = meta;

        TxtSubtitle.Text = meta.FileName;
        LoadHistory();
    }

    private void LoadHistory()
    {
        var items = _service.Backups.List(_meta.FilePath, _meta.RelativePath);

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
        if (HistoryList.SelectedItem is not PostBackupEntry entry)
            return;

        var result = MessageBox.Show(this,
            $"确定要恢复到 {entry.TimeDisplay} 的版本吗？\n\n" +
            "当前编辑器里的内容会被替换。恢复之前，当前文件也会自动备份一份。",
            "恢复历史版本", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            return;

        if (!_service.Backups.Restore(entry.FilePath, _meta.FilePath, _meta.RelativePath, out string error))
        {
            MessageBox.Show(this, "恢复失败：" + error, "恢复历史版本",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LogService.Info($"已恢复文章历史：{_meta.FileName} ← {Path.GetFileName(entry.FilePath)}");

        Restored = true;
        DialogResult = true;
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = _service.Backups.BackupRoot;
            Directory.CreateDirectory(dir);

            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开备份目录：" + ex.Message, "打开失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
