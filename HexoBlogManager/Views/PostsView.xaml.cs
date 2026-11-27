using System.Windows;
using System.Windows.Controls;
using HexoBlogManager.Models;
using HexoBlogManager.Services;

namespace HexoBlogManager.Views;

/// <summary>
/// 文章管理页：列表 + 搜索 + 新建/编辑/删除。
///
/// 数据流：
/// PostService.LoadAll() → _allPosts（全量缓存）→ ApplyFilter() 过滤 → 绑定到 DataGrid
/// 每次增删改之后重新扫描一遍，保证与磁盘一致（文章数量级通常是几十到几百，重扫很快）。
/// </summary>
public partial class PostsView : UserControl
{
    private readonly List<PostMeta> _allPosts = new();

    private PostService? _service;
    private string? _blogRoot;

    private bool _initialized;
    private bool _loading;

    public PostsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        // 用户在「站点与目录」里切换了博客后，本页要跟着刷新
        BlogContext.Instance.SiteConnected += OnSiteConnected;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        await ReloadAsync(showError: true);
    }

    private void OnSiteConnected(object? sender, BlogSiteInfo site)
    {
        // 事件理论上在 UI 线程，稳妥起见还是切一下
        Dispatcher.Invoke(() =>
        {
            if (_initialized)
                _ = ReloadAsync(showError: false);
        });
    }

    // ==================================================================
    // 数据加载
    // ==================================================================

    private async Task ReloadAsync(bool showError)
    {
        if (_loading) return;
        _loading = true;

        TxtBusy.Visibility = Visibility.Visible;

        try
        {
            // 用户如果直接打开本页（没经过站点页），先补一次站点校验
            var site = BlogContext.Instance.CurrentSite
                       ?? await BlogContext.Instance.EnsureSiteAsync();

            if (site is null)
            {
                _allPosts.Clear();
                _service = null;
                _blogRoot = null;
                ApplyFilter();
                ShowEmptyState("还没有连接博客目录。\n请先在左侧「站点与目录」里选择一个 Hexo 博客，\n再回到这里就能看到文章列表。");
                return;
            }

            _blogRoot = site.RootPath;
            _service = new PostService(site.RootPath);

            // 文件 IO 放到线程池，几百篇文章也不会卡界面
            var posts = await Task.Run(() => _service.LoadAll());

            _allPosts.Clear();
            _allPosts.AddRange(posts);

            ApplyFilter();
        }
        catch (Exception ex)
        {
            if (showError)
                ShowMessage($"读取文章列表失败：\n{ex.Message}\n\n" +
                            "常见原因：\n· source/_posts 目录没有读取权限\n· 目录被其他程序独占",
                    "出错了", MessageBoxImage.Error);
        }
        finally
        {
            _loading = false;
            TxtBusy.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>按当前搜索词过滤并刷新表格。</summary>
    private void ApplyFilter()
    {
        string keyword = SearchBox.Text ?? string.Empty;

        var filtered = _allPosts.Where(p => p.Matches(keyword)).ToList();
        PostsGrid.ItemsSource = filtered;

        bool searching = keyword.Trim().Length > 0;

        string summary = _allPosts.Count == 0
            ? "还没有文章"
            : searching
                ? $"共 {_allPosts.Count} 篇 · 匹配 {filtered.Count} 篇"
                : $"共 {_allPosts.Count} 篇文章 · 双击行可直接编辑";

        // 读取失败的文件不会出现在列表里，必须显式说一声，
        // 否则用户只会觉得「我明明有 30 篇，怎么只显示 29 篇」
        int skipped = _service?.LastSkippedFiles.Count ?? 0;

        if (skipped > 0)
        {
            summary += $" · {skipped} 个文件读取失败（悬停查看路径）";
            TxtCount.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger");
            TxtCount.ToolTip = string.Join("\n", _service!.LastSkippedFiles.Take(10));
        }
        else
        {
            TxtCount.Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextTertiary");
            TxtCount.ToolTip = null;
        }

        TxtCount.Text = summary;

        if (_allPosts.Count == 0)
            ShowEmptyState("source/_posts 下还没有文章。\n点击右上角「新建文章」开始写第一篇。");
        else
            EmptyPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowEmptyState(string message)
    {
        TxtEmpty.Text = message;
        EmptyPanel.Visibility = Visibility.Visible;
    }

    // ==================================================================
    // 交互
    // ==================================================================

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;

        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyFilter();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        _ = ReloadAsync(showError: true);
    }

    private async void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        if (_blogRoot is null)
        {
            ShowMessage("请先在「站点与目录」里选择博客目录。", "提示", MessageBoxImage.Information);
            return;
        }

        try
        {
            var editor = new PostEditorWindow(_blogRoot)
            {
                Owner = Window.GetWindow(this)
            };

            if (editor.ShowDialog() == true)
            {
                await ReloadAsync(showError: false);
                TxtCount.Text = $"已创建：{editor.SavedFileName}";
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"打开编辑器失败：\n{ex.Message}", "出错了", MessageBoxImage.Error);
        }
    }

    private async void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        await EditSelectedAsync();
    }

    private void PostsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _ = EditSelectedAsync();
    }

    private async Task EditSelectedAsync()
    {
        if (_blogRoot is null)
        {
            ShowMessage("请先在「站点与目录」里选择博客目录。", "提示", MessageBoxImage.Information);
            return;
        }

        if (PostsGrid.SelectedItem is not PostMeta post)
        {
            ShowMessage("请先在列表里选中一篇文章。", "提示", MessageBoxImage.Information);
            return;
        }

        try
        {
            var editor = new PostEditorWindow(_blogRoot, post)
            {
                Owner = Window.GetWindow(this)
            };

            if (editor.ShowDialog() == true)
                await ReloadAsync(showError: false);
        }
        catch (Exception ex)
        {
            ShowMessage($"打开编辑器失败：\n{ex.Message}", "出错了", MessageBoxImage.Error);
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null)
        {
            ShowMessage("请先在「站点与目录」里选择博客目录。", "提示", MessageBoxImage.Information);
            return;
        }

        if (PostsGrid.SelectedItem is not PostMeta post)
        {
            ShowMessage("请先在列表里选中要删除的文章。", "提示", MessageBoxImage.Information);
            return;
        }

        // 二次确认：把标题和路径都亮出来，避免删错
        var owner = Window.GetWindow(this);
        string message =
            $"确定要删除这篇文章吗？\n\n" +
            $"《{post.Title}》\n" +
            $"{post.RelativePath}\n\n" +
            "删除后会移入 Windows 回收站，可以从回收站还原。";

        var result = owner is null
            ? MessageBox.Show(message, "删除确认", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, message, "删除确认", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            if (_service.Delete(post, out string error))
            {
                _allPosts.Remove(post);
                ApplyFilter();
                TxtCount.Text = $"已删除《{post.Title}》";
            }
            else
            {
                ShowMessage($"删除失败：\n{error}", "出错了", MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"删除时出错：\n{ex.Message}", "出错了", MessageBoxImage.Error);
        }
    }

    // ==================================================================
    // 小工具
    // ==================================================================

    private void ShowMessage(string text, string caption, MessageBoxImage icon)
    {
        var owner = Window.GetWindow(this);
        if (owner is not null)
            MessageBox.Show(owner, text, caption, MessageBoxButton.OK, icon);
        else
            MessageBox.Show(text, caption, MessageBoxButton.OK, icon);
    }
}
