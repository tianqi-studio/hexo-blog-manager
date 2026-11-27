using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HexoBlogManager.Models;
using HexoBlogManager.Services;
using HexoBlogManager.Views;

namespace HexoBlogManager;

/// <summary>
/// 主窗口：左侧导航 + 右侧内容区。
///
/// 视图采用「懒加载 + 缓存」策略：
/// · 第一次切换到某个页面时才创建，避免启动时把所有页面都初始化一遍；
/// · 创建后缓存在字典里，来回切换不会丢失页面状态（比如文章列表的搜索词）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _viewCache = new();
    private bool _ready;

    /// <summary>当前显示的页面标识，用于判断「从哪一页离开」。</summary>
    private string? _currentKey;

    /// <summary>回退导航选中项时置位，避免再次触发导航逻辑。</summary>
    private bool _suppressNav;

    /// <summary>调试用：指定启动后默认打开哪个页面（site / posts / console / config）。</summary>
    public string? InitialPage { get; set; }

    /// <summary>调试用：启动后自动打开指定文章的编辑器（传文件名）。</summary>
    public string? DebugOpenEditor { get; set; }

    /// <summary>调试用：启动后自动在控制台执行一条 hexo 命令。</summary>
    public string? DebugAutorun { get; set; }

    /// <summary>调试/截图模式下跳过「预览服务运行中」的关闭确认，避免自动化流程被弹窗卡住。</summary>
    public bool SuppressCloseConfirm { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;

        // 站点连接成功后，刷新左下角的「当前博客」显示
        BlogContext.Instance.SiteConnected += OnSiteConnected;
        Closed += OnClosed;
        Closing += OnClosing;
    }

    /// <summary>窗口关闭：把控制台里的后台进程（hexo server）收干净，不留僵尸 node。</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        BlogContext.Instance.SiteConnected -= OnSiteConnected;

        foreach (var console in _viewCache.Values.OfType<ConsoleView>())
            console.Shutdown();
    }

    /// <summary>预览服务还在跑时，关闭前先确认一次，避免用户以为服务还在。</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (SuppressCloseConfirm)
            return;

        var console = _viewCache.Values.OfType<ConsoleView>().FirstOrDefault();
        if (console is null || !console.IsRunning)
            return;

        var result = MessageBox.Show(this,
            "本地预览服务正在运行。\n\n关闭窗口会一并停止该服务，确定要关闭吗？",
            "预览服务运行中", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        RefreshCurrentBlogLabel();

        // XAML 里 SelectedIndex=0 已在 InitializeComponent 阶段触发过一次 SelectionChanged，
        // 那时 MainContent 还没准备好，所以这里再主动导航一次，确保首屏正确。
        string target = string.IsNullOrWhiteSpace(InitialPage) ? "site" : InitialPage!;

        if (target == "site")
        {
            NavigateTo("site");
        }
        else
        {
            bool navigated = false;

            // 通过设置选中项触发 SelectionChanged，保证左侧导航高亮与内容一致
            foreach (object item in NavList.Items)
            {
                if (item is ListBoxItem listItem && (listItem.Tag as string) == target)
                {
                    NavList.SelectedItem = listItem;
                    navigated = true;
                    break;
                }
            }

            if (!navigated)
                NavigateTo("site");
        }

        if (!string.IsNullOrWhiteSpace(DebugOpenEditor))
            OpenDebugEditor(DebugOpenEditor!);

        if (!string.IsNullOrWhiteSpace(DebugAutorun) &&
            _viewCache.TryGetValue("console", out var consoleView) &&
            consoleView is ConsoleView console)
        {
            _ = console.RunForDebugAsync(DebugAutorun!);
        }
    }

    /// <summary>调试用：自动打开某篇文章的编辑器窗口。</summary>
    private async void OpenDebugEditor(string fileName)
    {
        try
        {
            var site = BlogContext.Instance.CurrentSite
                       ?? await BlogContext.Instance.EnsureSiteAsync();
            if (site is null) return;

            var service = new PostService(site.RootPath);
            var post = service.LoadAll().FirstOrDefault(p =>
                p.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                p.RelativePath.Equals(fileName, StringComparison.OrdinalIgnoreCase));

            if (post is null) return;

            var editor = new PostEditorWindow(site.RootPath, post);
            editor.Show();
        }
        catch
        {
            // 调试路径，失败静默
        }
    }

    private void OnSiteConnected(object? sender, BlogSiteInfo site)
    {
        // 事件可能在非 UI 线程触发，统一切回 UI 线程
        Dispatcher.Invoke(RefreshCurrentBlogLabel);
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;                       // 初始化阶段忽略
        if (MainContent is null) return;           // XAML 尚未构建完毕

        if (NavList.SelectedItem is ListBoxItem item && item.Tag is string key)
            NavigateTo(key);
    }

    private void NavigateTo(string key)
    {
        if (_suppressNav)
            return;

        // 配置页有未保存的修改时先确认：切换页面会卸载视图，但视图实例被缓存，
        // 改动其实还在内存里，用户回到配置页会看到「改过的样子」，
        // 却不知道这些改动从未落盘——比直接丢失更容易误导。
        if (_currentKey == "config" && key != "config" &&
            _viewCache.TryGetValue("config", out var cachedView) &&
            cachedView is ConfigView configView && configView.HasPendingEdits)
        {
            var result = MessageBox.Show(this,
                "配置页还有未保存的修改，切换页面后需要回到配置页点「保存修改」才会生效。\n\n要现在离开吗？",
                "有未保存的修改", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                // 把左侧高亮退回去，否则界面显示已切换、内容却没变
                _suppressNav = true;

                foreach (object obj in NavList.Items)
                {
                    if (obj is ListBoxItem item && (item.Tag as string) == _currentKey)
                    {
                        NavList.SelectedItem = item;
                        break;
                    }
                }

                _suppressNav = false;
                return;
            }
        }

        if (!_viewCache.TryGetValue(key, out var view))
        {
            view = CreateView(key);
            _viewCache[key] = view;
        }

        MainContent.Content = view;
        _currentKey = key;
        RefreshCurrentBlogLabel();
    }

    private static UserControl CreateView(string key) => key switch
    {
        "posts" => new PostsView(),

        "console" => new ConsoleView(),

        "config" => new ConfigView(),

        "publish" => new PublishView(),

        // 默认返回站点页
        _ => new SiteView()
    };

    /// <summary>刷新左下角当前博客显示。</summary>
    private void RefreshCurrentBlogLabel()
    {
        string? path = BlogContext.Instance.CurrentBlogPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            TxtCurrentBlog.Text = "未选择";
            TxtCurrentBlog.ToolTip = "还没有选择博客目录";
            return;
        }

        // 主行只显示目录名，完整路径放在 ToolTip 里，避免长路径把侧栏撑变形
        string name;
        try
        {
            name = new DirectoryInfo(path).Name;
            if (string.IsNullOrWhiteSpace(name)) name = path;
        }
        catch
        {
            name = path;
        }

        TxtCurrentBlog.Text = name;
        TxtCurrentBlog.ToolTip = path;
    }

    /// <summary>在资源管理器里打开日志目录。</summary>
    private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (LogService.OpenLogFolder(out string error))
        {
            LogService.Info("用户从侧栏打开了日志目录");
            return;
        }

        MessageBox.Show(this, "无法打开日志目录：" + error, "打开失败",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
