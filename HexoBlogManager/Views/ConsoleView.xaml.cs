using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using HexoBlogManager.Services;

namespace HexoBlogManager.Views;

/// <summary>
/// 命令控制台页。
///
/// 结构：命令按钮 → 预览地址卡（server 启动后出现）→ 深色实时日志。
/// 亮点：
/// · 日志按 INFO / WARN / ERROR 分级着色
/// · hexo server 输出里一旦出现本地地址，自动提取并展示「打开浏览器」
/// · 停止按钮直接杀进程树，hexo server 的 node 子进程不会残留
/// · 命令失败时给出针对该命令的中文排查方向
/// </summary>
public partial class ConsoleView : UserControl
{
    // 深色背景下的日志配色（参考 VS Code 终端）
    private static readonly Brush BrushInfo = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
    private static readonly Brush BrushSuccess = new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
    private static readonly Brush BrushWarning = new SolidColorBrush(Color.FromRgb(0xD7, 0xBA, 0x7D));
    private static readonly Brush BrushError = new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71));
    private static readonly Brush BrushSystem = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
    private static readonly Brush BrushGreen = new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x0F));

    private const int MaxLogLines = 3000;

    private readonly HexoCommandRunner _runner = new();
    private readonly List<(LogLevel Level, string Text)> _logLines = new();

    private string _currentCommand = string.Empty;
    private string? _previewUrl;
    private bool _initialized;

    /// <summary>本次执行期间是否出现过 ERROR 级日志。
    /// 有些 hexo 命令（比如 deployer 缺失）会打 ERROR 但退出码仍是 0，
    /// 只靠退出码判断会误报「执行成功」。</summary>
    private bool _hadError;

    public ConsoleView()
    {
        InitializeComponent();

        // 自建空文档：RichTextBox 默认带一个空段落，会让首行多出间距
        LogBox.Document = new FlowDocument
        {
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI"),
            FontSize = 12.5,
            // 四周留白交给 RichTextBox 的 Padding：两者同时生效会让滚动范围多算一段，
            // 首行和末行会被裁掉半行（这个问题在最开始就是这么来的）
            PagePadding = new Thickness(0)
        };

        _runner.OutputReceived += OnRunnerOutput;
        _runner.ProcessExited += OnRunnerExited;

        Loaded += OnLoaded;

        SetRunning(false);
        AppendSystem("就绪。选择上方的命令开始执行。");
    }

    /// <summary>预览服务是否正在运行（主窗口关闭时用来提示）。</summary>
    public bool IsRunning => _runner.IsRunning;

    /// <summary>程序退出时调用，确保不留下后台 node 进程。</summary>
    public void Shutdown()
    {
        try { _runner.Stop(); } catch { }
        _runner.Dispose();
    }

    /// <summary>调试用：由启动参数触发执行一条命令。</summary>
    public Task RunForDebugAsync(string command)
    {
        string display = command switch
        {
            "clean" => "清理缓存",
            "generate" => "生成站点",
            "server" => "启动预览",
            "deploy" => "部署站点",
            _ => command
        };

        return RunHexoAsync(command, display);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        // 如果用户是直接打开本页，先确保站点可用，尽早给出提示
        var site = BlogContext.Instance.CurrentSite ?? await BlogContext.Instance.EnsureSiteAsync();
        if (site is null)
            AppendWarning("还没有连接博客目录，请先在「站点与目录」里选择一个 Hexo 博客。");
        else
            AppendSystem("当前博客：" + site.RootPath);
    }

    // ==================================================================
    // 命令执行
    // ==================================================================

    private void BtnClean_Click(object sender, RoutedEventArgs e)
        => _ = RunHexoAsync("clean", "清理缓存");

    private void BtnGenerate_Click(object sender, RoutedEventArgs e)
        => _ = RunHexoAsync("generate", "生成站点");

    private void BtnServer_Click(object sender, RoutedEventArgs e)
        => _ = RunHexoAsync("server", "启动预览");

    private void BtnDeploy_Click(object sender, RoutedEventArgs e)
        => _ = RunHexoAsync("deploy", "部署站点");

    private async Task RunHexoAsync(string command, string displayName)
    {
        if (_runner.IsRunning) return;

        var site = BlogContext.Instance.CurrentSite ?? await BlogContext.Instance.EnsureSiteAsync();
        if (site is null)
        {
            AppendWarning("还没有连接博客目录，请先在「站点与目录」里选择一个 Hexo 博客。");
            return;
        }

        _currentCommand = command;
        _hadError = false;
        HidePreviewUrl();

        AppendSystem(string.Empty);
        AppendSystem($"$ hexo {command}    （{displayName}）");
        AppendSystem($"  工作目录：{site.RootPath}");

        if (_runner.Start($"hexo {command}", site.RootPath, out string error))
        {
            SetRunning(true);
        }
        else
        {
            AppendError(error);
            SetRunning(false);
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (!_runner.IsRunning)
            return;

        AppendSystem("正在停止…");
        _runner.Stop();
    }

    private void OnRunnerOutput(object? sender, LogEventArgs e)
    {
        // 事件来自后台读取线程，必须切回 UI 线程
        Dispatcher.BeginInvoke(() =>
        {
            if (e.Level == LogLevel.Error)
                _hadError = true;

            AppendLog(e.Level, e.Text);

            // hexo server 会输出 "Hexo is running at http://localhost:4000/"
            if (_currentCommand == "server")
            {
                string? url = HexoCommandRunner.TryExtractLocalUrl(e.Text);
                if (url is not null)
                    ShowPreviewUrl(url);
            }
        });
    }

    private void OnRunnerExited(object? sender, int code)
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool stoppedByUser = _runner.StopRequested;
            SetRunning(false);

            if (stoppedByUser)
            {
                AppendSystem("服务已停止。");
                HidePreviewUrl();
                return;
            }

            if (code == 0)
            {
                if (_hadError)
                {
                    // 退出码 0 但日志里有 ERROR：Hexo 的常见行为，必须提醒用户
                    AppendWarning("命令虽然正常结束（退出码 0），但日志中出现了错误信息，请检查上面的红色内容。");
                    AppendHint(_currentCommand);
                }
                else
                {
                    AppendSuccess("命令执行完成（退出码 0）。");
                }
            }
            else
            {
                AppendError($"命令执行失败，退出码 {code}。");
                AppendHint(_currentCommand);
                HidePreviewUrl();
            }
        });
    }

    /// <summary>针对不同命令给出可操作的中文排查方向。</summary>
    private void AppendHint(string command)
    {
        string hint = command switch
        {
            "clean" =>
                "排查方向：\n" +
                "  · public 目录或 db.json 被其他程序占用（关闭编辑器 / 资源管理器预览后重试）\n" +
                "  · 博客目录没有写权限",

            "generate" =>
                "排查方向：\n" +
                "  · 主题未安装：npm install hexo-theme-butterfly\n" +
                "  · 依赖缺失：在博客根目录执行 npm install\n" +
                "  · 文章 front-matter 格式错误（缩进不对、冒号后缺空格）\n" +
                "  · 渲染器缺失：npm install hexo-renderer-pug hexo-renderer-stylus",

            "server" =>
                "排查方向：\n" +
                "  · 4000 端口被占用，换端口：hexo server -p 5000\n" +
                "  · 未安装 hexo-server：npm install hexo-server\n" +
                "  · 端口被本工具的旧进程占用，先点「停止」再重试",

            "deploy" =>
                "排查方向：\n" +
                "  · _config.yml 的 deploy 段没配置\n" +
                "  · 未安装部署插件：npm install hexo-deployer-git\n" +
                "  · Git 未设置 user.name / user.email，或没有仓库推送权限",

            _ => string.Empty
        };

        if (hint.Length > 0)
            AppendSystem(hint);
    }

    // ==================================================================
    // 预览地址
    // ==================================================================

    private void ShowPreviewUrl(string url)
    {
        // 去掉末尾斜杠，显示更干净
        _previewUrl = url.TrimEnd('/');
        TxtUrl.Text = _previewUrl;
        PanelUrl.Visibility = Visibility.Visible;
    }

    private void HidePreviewUrl()
    {
        _previewUrl = null;
        PanelUrl.Visibility = Visibility.Collapsed;
    }

    private void BtnOpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_previewUrl))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(_previewUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendError($"打开浏览器失败：{ex.Message}");
        }
    }

    // ==================================================================
    // 日志
    // ==================================================================

    private void AppendLog(LogLevel level, string text)
    {
        // 剪贴板/分级判断都用纯文本，界面上再按 ANSI 还原 Hexo 原本的配色
        _logLines.Add((level, AnsiText.Strip(text)));

        Brush fallbackBrush = BrushFor(level);
        Color fallbackColor = (fallbackBrush as SolidColorBrush)?.Color ?? Colors.Gainsboro;

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = 18
        };

        foreach (var segment in AnsiText.Parse(text, fallbackColor))
        {
            var run = new Run(segment.Text)
            {
                Foreground = new SolidColorBrush(segment.Foreground ?? fallbackColor)
            };

            if (segment.Bold)
                run.FontWeight = FontWeights.SemiBold;

            paragraph.Inlines.Add(run);
        }

        // 空行也要占一行高度，否则日志会出现「塌陷」
        if (paragraph.Inlines.Count == 0)
            paragraph.Inlines.Add(new Run(" "));

        LogBox.Document.Blocks.Add(paragraph);

        TrimIfNeeded();

        AutoScrollToEnd();
    }

    /// <summary>
    /// 自动滚到底。
    /// 直接 ScrollToEnd 会少滚半行（新段落还没进布局，滚动范围算小了），
    /// 所以先滚一次，再在布局完成后的空闲时机 UpdateLayout 后补一次。
    /// </summary>
    private void AutoScrollToEnd()
    {
        if (ChkAutoScroll.IsChecked != true)
            return;

        LogBox.ScrollToEnd();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            LogBox.UpdateLayout();
            LogBox.ScrollToEnd();
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void AppendSystem(string text) => AppendLog(LogLevel.System, text);
    private void AppendSuccess(string text) => AppendLog(LogLevel.Success, text);
    private void AppendWarning(string text) => AppendLog(LogLevel.Warning, text);
    private void AppendError(string text) => AppendLog(LogLevel.Error, text);

    /// <summary>日志超过上限时，从头部批量删除，避免长时间运行把内存吃满。</summary>
    private void TrimIfNeeded()
    {
        if (_logLines.Count <= MaxLogLines)
            return;

        int removeCount = _logLines.Count - MaxLogLines + 500;
        _logLines.RemoveRange(0, removeCount);

        for (int i = 0; i < removeCount; i++)
        {
            var first = LogBox.Document.Blocks.FirstBlock;
            if (first is null) break;
            LogBox.Document.Blocks.Remove(first);
        }
    }

    private static Brush BrushFor(LogLevel level) => level switch
    {
        LogLevel.Success => BrushSuccess,
        LogLevel.Warning => BrushWarning,
        LogLevel.Error => BrushError,
        LogLevel.System => BrushSystem,
        _ => BrushInfo
    };

    private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logLines.Count == 0)
        {
            AppToast("日志是空的。");
            return;
        }

        try
        {
            var sb = new StringBuilder();
            foreach (var (_, text) in _logLines)
                sb.AppendLine(text);

            Clipboard.SetText(sb.ToString());
            AppToast($"已复制 {_logLines.Count} 行日志到剪贴板。");
        }
        catch (Exception ex)
        {
            AppendError($"复制失败：{ex.Message}");
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Document.Blocks.Clear();
        _logLines.Clear();
        AppendSystem("日志已清空。");
    }

    /// <summary>往控制台里打一条提示，避免用弹窗打断操作。</summary>
    private void AppToast(string message) => AppendSystem(message);

    // ==================================================================
    // 状态
    // ==================================================================

    private void SetRunning(bool running)
    {
        BtnClean.IsEnabled = !running;
        BtnGenerate.IsEnabled = !running;
        BtnServer.IsEnabled = !running;
        BtnDeploy.IsEnabled = !running;
        BtnStop.IsEnabled = running;

        StatusDot.Fill = running ? BrushGreen : (Brush)FindResource("Brush.TextTertiary");
        TxtStatus.Text = running ? "执行中…" : "空闲";
        TxtStatus.Foreground = running
            ? BrushGreen
            : (Brush)FindResource("Brush.TextSecondary");
    }
}
