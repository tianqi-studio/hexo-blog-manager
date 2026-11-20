using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HexoBlogManager.Models;
using HexoBlogManager.Services;

// WPF 的 System.Windows.Controls 里也有一个 ValidationResult（用于数据绑定校验），
// 与我们的模型同名，这里用别名明确指向自己的类型，避免歧义。
using ValidationResult = HexoBlogManager.Models.ValidationResult;

namespace HexoBlogManager.Views;

/// <summary>
/// 「站点与目录」页。
///
/// 职责：
/// 1) 启动时自动加载上次保存的博客路径并校验
/// 2) 让用户选择新的博客目录，选择后立即校验
/// 3) 展示站点信息（标题、主题、文章数等）
/// 4) 展示 Node / hexo 环境自检结果，失败时给出中文排查建议
///
/// 线程模型：
/// · 目录校验、命令探测都是异步的，不阻塞 UI
/// · 文件 IO 用 Task.Run 丢到线程池，避免网络盘卡住界面
/// </summary>
public partial class SiteView : UserControl
{
    // ---- 状态点 / 徽章配色（统一在这里定义，避免散落各处） ----
    private static readonly Color ColorSuccess = Color.FromRgb(0x0F, 0x7B, 0x0F);
    private static readonly Color ColorDanger = Color.FromRgb(0xC4, 0x2B, 0x1C);
    private static readonly Color ColorNeutral = Color.FromRgb(0x6A, 0x6F, 0x76);
    private static readonly Color ColorSuccessBg = Color.FromRgb(0xE7, 0xF5, 0xE7);
    private static readonly Color ColorDangerBg = Color.FromRgb(0xFD, 0xEC, 0xEA);
    private static readonly Color ColorNeutralBg = Color.FromRgb(0xEF, 0xF1, 0xF4);

    private readonly BlogSiteService _siteService = new();
    private readonly EnvironmentProbe _probe = new();

    private bool _initialized;      // 防止 Loaded 多次触发重复初始化
    private bool _busy;             // 校验进行中，避免重复点击
    private string? _currentPath;   // 当前界面上展示的路径（可能尚未校验通过）

    public SiteView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // ==================================================================
    // 初始化
    // ==================================================================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            var settings = new SettingsService();
            TxtSettingsPath.Text =
                $"设置文件：{settings.SettingsFilePath}（删除该文件可重置所有记录）";

            // 恢复上次使用的博客目录
            string? saved = BlogContext.Instance.SavedBlogPath;
            if (!string.IsNullOrWhiteSpace(saved))
            {
                _currentPath = saved;
                TxtPath.Text = saved;
                await ValidateAsync(saved, fromUser: false);
            }
            else
            {
                ShowNotConfigured();
            }

            // 环境检测独立进行，不等待目录校验结果
            StartEnvironmentProbe();
        }
        catch (Exception ex)
        {
            ShowMessage($"初始化站点页面时出错：\n{ex.Message}", "出错了", MessageBoxImage.Error);
        }
    }

    // ==================================================================
    // 目录校验
    // ==================================================================

    /// <summary>校验指定目录并更新界面。</summary>
    /// <param name="path">要校验的目录。</param>
    /// <param name="fromUser">是否由用户主动触发（是的话，失败时弹窗说明）。</param>
    private async Task ValidateAsync(string path, bool fromUser)
    {
        if (_busy) return;

        SetBusy(true, "校验中…");

        ValidationResult result;
        try
        {
            // 目录可能位于网络驱动器或慢速机械盘，放到线程池执行
            result = await Task.Run(() => _siteService.Validate(path));
        }
        catch (Exception ex)
        {
            result = ValidationResult.Fail("校验目录时发生异常", ex.Message);
        }

        SetBusy(false, null);
        _currentPath = path;
        TxtPath.Text = path;
        ApplyResult(result);

        if (!result.IsValid)
        {
            // 校验失败：清掉内存里的站点状态，但保留磁盘上的路径记录，
            // 方便用户插好移动硬盘后直接点「重新校验」。
            BlogContext.Instance.ClearCurrentSite();
            ResetBlogCommandLine("目录不可用，无法检测");

            if (fromUser)
            {
                ShowMessage(
                    $"这个目录不能作为博客根目录使用。\n\n【{result.Message}】\n\n{result.Hint}",
                    "目录校验未通过", MessageBoxImage.Warning);
            }
            return;
        }

        // 校验通过：写入全局状态并落盘
        BlogContext.Instance.SetCurrentSite(result.Site!);

        // 顺带验证「在这个目录下执行 hexo 命令」是否正常
        await RunBlogCommandProbeAsync(result.Site!.RootPath);
    }

    /// <summary>把校验结果反映到界面上。</summary>
    private void ApplyResult(ValidationResult result)
    {
        if (result.IsValid && result.Site is not null)
        {
            SetPill("已连接", ColorSuccessBg, ColorSuccess);
            PanelNotice.Visibility = Visibility.Collapsed;

            CardSite.Visibility = Visibility.Visible;
            FillSiteInfo(result.Site);

            BtnOpen.IsEnabled = true;
            return;
        }

        SetPill("校验失败", ColorDangerBg, ColorDanger);
        CardSite.Visibility = Visibility.Collapsed;
        BtnOpen.IsEnabled = Directory.Exists(_currentPath);

        TxtNoticeTitle.Text = result.Message;
        TxtNoticeHint.Text = result.Hint;
        PanelNotice.Visibility = Visibility.Visible;
    }

    /// <summary>填充站点信息卡片。</summary>
    private void FillSiteInfo(BlogSiteInfo site)
    {
        TxtSiteTitle.Text = OrPlaceholder(site.Title);
        TxtSubtitle.Text = OrPlaceholder(site.Subtitle);
        TxtAuthor.Text = OrPlaceholder(site.Author);

        bool themeMissing = !string.IsNullOrWhiteSpace(site.Theme) && !site.ThemeInstalled;
        TxtTheme.Text = string.IsNullOrWhiteSpace(site.Theme)
            ? "（未设置）"
            : site.Theme + (themeMissing ? "（未安装）" : string.Empty);

        TxtPostCount.Text = site.PostCount == 1 ? "1 篇" : $"{site.PostCount} 篇";
        TxtConfigTime.Text = site.ConfigLastWrite?.ToString("yyyy-MM-dd HH:mm") ?? "未知";

        if (themeMissing)
        {
            TxtThemeWarning.Text =
                $"在 themes\\ 和 node_modules\\ 下都没有找到主题「{site.Theme}」。\n" +
                "generate 时可能会退回默认主题或直接报错。\n\n" +
                "解决办法（任选其一）：\n" +
                "· 在博客根目录执行：npm install hexo-theme-" + site.Theme + "\n" +
                "· 或把主题文件夹放到 themes\\" + site.Theme + " 下";
            PanelThemeWarning.Visibility = Visibility.Visible;
        }
        else
        {
            PanelThemeWarning.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowNotConfigured()
    {
        _currentPath = null;
        TxtPath.Text = "尚未选择目录";
        SetPill("未连接", ColorNeutralBg, ColorNeutral);
        PanelNotice.Visibility = Visibility.Collapsed;
        CardSite.Visibility = Visibility.Collapsed;
        BtnOpen.IsEnabled = false;
        ResetBlogCommandLine("选择目录后检测");
    }

    // ==================================================================
    // 按钮事件
    // ==================================================================

    private async void BtnSelect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // OpenFolderDialog 是 .NET 8 起 WPF 自带的现代文件夹选择器（IFileDialog），
            // 比老的 WinForms FolderBrowserDialog 更符合 Windows 交互习惯。
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择 Hexo 博客根目录（包含 _config.yml 的那一层）",
                Multiselect = false
            };

            if (!string.IsNullOrWhiteSpace(_currentPath) && Directory.Exists(_currentPath))
            {
                dialog.InitialDirectory = _currentPath;
            }
            else
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (Directory.Exists(docs))
                    dialog.InitialDirectory = docs;
            }

            var owner = Window.GetWindow(this);
            bool? ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            if (ok != true || string.IsNullOrWhiteSpace(dialog.FolderName))
                return;

            await ValidateAsync(dialog.FolderName, fromUser: true);
        }
        catch (Exception ex)
        {
            ShowMessage($"打开文件夹选择器时出错：\n{ex.Message}", "出错了", MessageBoxImage.Error);
        }
    }

    private async void BtnRecheck_Click(object sender, RoutedEventArgs e)
    {
        string? path = _currentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowMessage("还没有选择目录，请先点击「选择博客文件夹」。", "提示", MessageBoxImage.Information);
            return;
        }

        await ValidateAsync(path, fromUser: true);
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        string? path = _currentPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowMessage("目录不存在，无法打开。\n\n可能目录已被移动或删除，请重新选择。",
                "提示", MessageBoxImage.Warning);
            return;
        }

        try
        {
            // 用 explorer.exe 打开，参数整体加引号，兼容带空格和中文的路径
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowMessage($"打开资源管理器失败：\n{ex.Message}", "出错了", MessageBoxImage.Error);
        }
    }

    private async void BtnRecheckEnv_Click(object sender, RoutedEventArgs e)
    {
        StartEnvironmentProbe();

        var site = BlogContext.Instance.CurrentSite;
        if (site is not null)
            await RunBlogCommandProbeAsync(site.RootPath);
        else
            ResetBlogCommandLine("选择目录后检测");
    }

    // ==================================================================
    // 环境自检
    // ==================================================================

    private void StartEnvironmentProbe()
    {
        // 两个探测并行跑，各自更新自己那一行，互不阻塞
        _ = ProbeNodeLineAsync();
        _ = ProbeHexoLineAsync();
    }

    private async Task ProbeNodeLineAsync()
    {
        SetLinePending(DotNode, TxtNode, TxtNodeHint, "检测中…");
        try
        {
            var status = await _probe.ProbeNodeAsync();
            ApplyLine(DotNode, TxtNode, TxtNodeHint, status);
        }
        catch (Exception ex)
        {
            ApplyLine(DotNode, TxtNode, TxtNodeHint,
                new ToolStatus("Node.js", false, string.Empty, $"检测失败：{ex.Message}"));
        }
    }

    private async Task ProbeHexoLineAsync()
    {
        SetLinePending(DotHexo, TxtHexo, TxtHexoHint, "检测中…");
        try
        {
            var status = await _probe.ProbeHexoAsync();
            ApplyLine(DotHexo, TxtHexo, TxtHexoHint, status);
        }
        catch (Exception ex)
        {
            ApplyLine(DotHexo, TxtHexo, TxtHexoHint,
                new ToolStatus("hexo-cli", false, string.Empty, $"检测失败：{ex.Message}"));
        }
    }

    private async Task RunBlogCommandProbeAsync(string blogRoot)
    {
        SetLinePending(DotBlogCmd, TxtBlogCmd, TxtBlogCmdHint, "检测中…");
        try
        {
            var status = await _probe.ProbeHexoInBlogAsync(blogRoot);
            ApplyLine(DotBlogCmd, TxtBlogCmd, TxtBlogCmdHint, status);
        }
        catch (Exception ex)
        {
            ApplyLine(DotBlogCmd, TxtBlogCmd, TxtBlogCmdHint,
                new ToolStatus("博客命令", false, string.Empty, $"检测失败：{ex.Message}"));
        }
    }

    // ==================================================================
    // 界面小工具
    // ==================================================================

    private void SetBusy(bool busy, string? pillText)
    {
        _busy = busy;
        BtnSelect.IsEnabled = !busy;
        BtnRecheck.IsEnabled = !busy;
        BtnRecheckEnv.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;

        if (busy && pillText is not null)
            SetPill(pillText, ColorNeutralBg, ColorNeutral);
    }

    private void SetPill(string text, Color background, Color foreground)
    {
        PillStatus.Background = new SolidColorBrush(background);
        TxtPillStatus.Foreground = new SolidColorBrush(foreground);
        TxtPillStatus.Text = text;
    }

    /// <summary>把某一行状态置为「检测中」。</summary>
    private void SetLinePending(Ellipse dot, TextBlock value, TextBlock hint, string text)
    {
        dot.Fill = new SolidColorBrush(ColorNeutral);
        value.Foreground = (Brush)FindResource("Brush.TextSecondary");
        value.Text = text;
        hint.Visibility = Visibility.Collapsed;
    }

    /// <summary>把探测结果写到某一行。</summary>
    private void ApplyLine(Ellipse dot, TextBlock value, TextBlock hint, ToolStatus status)
    {
        if (status.Available)
        {
            dot.Fill = new SolidColorBrush(ColorSuccess);
            value.Foreground = (Brush)FindResource("Brush.TextSecondary");
            value.Text = string.IsNullOrWhiteSpace(status.Version) ? "可用" : status.Version;
            hint.Visibility = Visibility.Collapsed;
        }
        else
        {
            dot.Fill = new SolidColorBrush(ColorDanger);
            value.Foreground = new SolidColorBrush(ColorDanger);
            value.Text = "不可用";

            if (string.IsNullOrWhiteSpace(status.Error))
            {
                hint.Visibility = Visibility.Collapsed;
            }
            else
            {
                hint.Text = status.Error;
                hint.Visibility = Visibility.Visible;
            }
        }
    }

    private void ResetBlogCommandLine(string text)
    {
        DotBlogCmd.Fill = new SolidColorBrush(ColorNeutral);
        TxtBlogCmd.Foreground = (Brush)FindResource("Brush.TextSecondary");
        TxtBlogCmd.Text = text;
        TxtBlogCmdHint.Visibility = Visibility.Collapsed;
    }

    private static string OrPlaceholder(string? value)
        => string.IsNullOrWhiteSpace(value) ? "（未设置）" : value;

    /// <summary>统一的弹窗入口：有父窗口时用父窗口做 owner，保证弹窗居中且置顶。</summary>
    private void ShowMessage(string text, string caption, MessageBoxImage icon)
    {
        var owner = Window.GetWindow(this);
        if (owner is not null)
            MessageBox.Show(owner, text, caption, MessageBoxButton.OK, icon);
        else
            MessageBox.Show(text, caption, MessageBoxButton.OK, icon);
    }
}
