using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using HexoBlogManager.Models;
using HexoBlogManager.Services;

namespace HexoBlogManager.Views;

/// <summary>
/// 发布到 GitHub Pages 页。
///
/// 交互顺序：
/// 1) 填仓库、Token（其余用默认值）；
/// 2) 点「检测仓库」先确认 Token 好使、仓库存在 —— 这一步能提前暴露 90% 的问题；
/// 3) 点「一键发布」：生成 → 提交 → 推送 → 开启 Pages，全程日志实时可见。
/// </summary>
public partial class PublishView : UserControl
{
    // 深色日志区配色（与命令控制台保持一致）
    private static readonly Brush BrushInfo = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
    private static readonly Brush BrushSuccess = new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
    private static readonly Brush BrushWarning = new SolidColorBrush(Color.FromRgb(0xD7, 0xBA, 0x7D));
    private static readonly Brush BrushError = new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71));
    private static readonly Brush BrushSystem = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
    private static readonly Brush BrushGreen = new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x0F));

    private const int MaxLogLines = 3000;

    /// <summary>调试用：为 true 时跳过所有确认弹窗（截图 / 自动化验证用），并自动同意创建仓库。</summary>
    public static bool DebugMode { get; set; }

    /// <summary>调试用：打开页面后自动执行一次发布。</summary>
    public static bool DebugAutoPublish { get; set; }

    /// <summary>调试用：打开页面后自动执行一次「检测仓库」。</summary>
    public static bool DebugAutoCheck { get; set; }

    private readonly SettingsService _settings = new();
    private readonly GitHubPublisher _publisher = new();
    private readonly List<(LogLevel Level, string Text)> _logLines = new();

    private GitHubPublishSettings _saved = new();
    private CancellationTokenSource? _runCts;

    private string _siteUrl = string.Empty;
    private bool _busy;
    private bool _initialized;

    public PublishView()
    {
        InitializeComponent();

        LogBox.Document = new FlowDocument
        {
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Microsoft YaHei UI"),
            FontSize = 12.5,
            // 四周留白交给 RichTextBox 的 Padding，文档本身不再设 PagePadding，
            // 否则两者同时生效会让滚动范围多算一段，首行/末行被裁掉半行。
            PagePadding = new Thickness(0)
        };

        _publisher.OutputReceived += (_, e) =>
            Dispatcher.BeginInvoke(() => AppendLog(e.Level, e.Text));

        Loaded += OnLoaded;
        Unloaded += (_, _) => _publisher.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        LoadSettings();

        AppendSystem("就绪。填写仓库与 Token 后，点「检测仓库」确认环境，再点「一键发布」。");

        var site = BlogContext.Instance.CurrentSite ?? await BlogContext.Instance.EnsureSiteAsync();
        if (site is null)
            AppendWarning("还没有连接博客目录，请先到「站点与目录」里选择一个 Hexo 博客。");
        else
            AppendSystem("当前博客：" + site.RootPath);

        if (DebugAutoCheck)
        {
            await Task.Delay(600);
            await CheckRepoAsync();
        }

        if (DebugAutoPublish)
        {
            await Task.Delay(1200);
            await PublishAsync();
        }
    }

    // ==================================================================
    // 设置的读取与保存
    // ==================================================================

    private void LoadSettings()
    {
        try
        {
            _saved = _settings.LoadGitHub();
        }
        catch
        {
            _saved = new GitHubPublishSettings();
        }

        TxtRepo.Text = string.IsNullOrWhiteSpace(_saved.Owner)
            ? string.Empty
            : $"{_saved.Owner}/{_saved.Repo}";

        TxtBranch.Text = string.IsNullOrWhiteSpace(_saved.Branch) ? "gh-pages" : _saved.Branch;
        TxtCommit.Text = string.IsNullOrWhiteSpace(_saved.CommitMessage)
            ? "Site updated: {date}"
            : _saved.CommitMessage;
        TxtProxy.Text = _saved.Proxy;
        TxtRemote.Text = _saved.RemoteUrl;

        ChkClean.IsChecked = _saved.CleanBeforeBuild;
        ChkNoJekyll.IsChecked = _saved.WriteNoJekyll;
        ChkAutoPages.IsChecked = _saved.AutoEnablePages;

        string token = SecretProtector.Unprotect(_saved.EncryptedToken);
        if (token.Length > 0)
        {
            PwdToken.Password = token;
            AppendSuccess("已载入本机保存的 Token（用 Windows 加密存储，不会明文落盘）。");
        }

        // 上次发布过的话，把地址先显示出来，方便直接打开
        if (!string.IsNullOrWhiteSpace(_saved.LastPublishedUrl))
        {
            ShowResult("上次发布", _saved.LastPublishedUrl);
            TxtStatus.Text = _saved.LastPublishedAt is null
                ? "已配置"
                : "上次发布：" + _saved.LastPublishedAt.Value.LocalDateTime.ToString("MM-dd HH:mm");
        }
    }

    private void SaveSettings()
    {
        var (owner, repo) = GitHubPublisher.TryParseRepoInput(TxtRepo.Text);

        _saved = new GitHubPublishSettings
        {
            Owner = owner,
            Repo = repo,
            Branch = string.IsNullOrWhiteSpace(TxtBranch.Text) ? "gh-pages" : TxtBranch.Text.Trim(),
            CommitMessage = string.IsNullOrWhiteSpace(TxtCommit.Text) ? "Site updated: {date}" : TxtCommit.Text.Trim(),
            EncryptedToken = SecretProtector.Protect(PwdToken.Password.Trim()),
            CleanBeforeBuild = ChkClean.IsChecked == true,
            WriteNoJekyll = ChkNoJekyll.IsChecked == true,
            AutoEnablePages = ChkAutoPages.IsChecked == true,
            RemoteUrl = TxtRemote.Text.Trim(),
            Proxy = TxtProxy.Text.Trim(),
            LastPublishedAt = _saved.LastPublishedAt,
            LastPublishedUrl = _saved.LastPublishedUrl
        };

        if (!_settings.SaveGitHub(_saved, out string error) && error.Length > 0)
            AppendWarning("配置保存失败：" + error);
    }

    private PublishOptions CollectOptions()
    {
        var (owner, repo) = GitHubPublisher.TryParseRepoInput(TxtRepo.Text);

        return new PublishOptions
        {
            Owner = owner,
            Repo = repo,
            Branch = string.IsNullOrWhiteSpace(TxtBranch.Text) ? "gh-pages" : TxtBranch.Text.Trim(),
            CommitMessage = TxtCommit.Text.Trim(),
            Token = PwdToken.Password.Trim(),
            Proxy = TxtProxy.Text.Trim(),
            RemoteUrl = TxtRemote.Text.Trim(),
            CleanBeforeBuild = ChkClean.IsChecked == true,
            WriteNoJekyll = ChkNoJekyll.IsChecked == true,
            AutoEnablePages = ChkAutoPages.IsChecked == true,
            BlogRoot = BlogContext.Instance.CurrentBlogPath ?? string.Empty
        };
    }

    /// <summary>发布前的最小校验，返回 false 时 error 里是可以直接展示的中文原因。</summary>
    private static bool Validate(PublishOptions options, out string error)
    {
        error = string.Empty;

        bool hasRemote = !string.IsNullOrWhiteSpace(options.RemoteUrl);
        bool hasRepo = !string.IsNullOrWhiteSpace(options.Owner) && !string.IsNullOrWhiteSpace(options.Repo);

        if (!hasRemote && !hasRepo)
        {
            error = "请先填写仓库，格式：用户名/仓库名（例如 octocat/my-blog）。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.Branch))
        {
            error = "请填写发布分支，通常用 gh-pages。";
            return false;
        }

        // 推送 GitHub 必须带 Token：没有 Token 时 git 会弹凭据窗口，
        // 在我们这个隐藏窗口的进程里会一直卡住，必须提前拦住。
        bool needsToken = hasRemote
            ? options.RemoteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
              options.RemoteUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase)
            : true;

        if (needsToken && string.IsNullOrWhiteSpace(options.Token))
        {
            error = "请填写 Access Token。\n" +
                    "生成方式：GitHub → Settings → Developer settings → Personal access tokens → " +
                    "Tokens (classic) → Generate new token，勾选 repo 权限。";
            return false;
        }

        return true;
    }

    // ==================================================================
    // 检测仓库
    // ==================================================================

    private async void BtnCheck_Click(object sender, RoutedEventArgs e) => await CheckRepoAsync();

    private async Task CheckRepoAsync()
    {
        if (_busy) return;

        var options = CollectOptions();
        if (!Validate(options, out string error))
        {
            AppendError(error);
            return;
        }

        SetBusy(true, "检测中…");

        string finalStatus = "检测完成";

        try
        {
            SaveSettings();

            using var api = new GitHubApi(options.Token, options.Proxy);

            AppendSystem(string.Empty);
            AppendSystem("$ 校验 Token");

            string login = await api.GetLoginAsync();
            AppendSuccess($"Token 有效，当前身份：{login}");

            if (!string.IsNullOrWhiteSpace(options.Owner) &&
                !login.Equals(options.Owner, StringComparison.OrdinalIgnoreCase))
            {
                AppendWarning($"提醒：Token 属于「{login}」，而仓库填的是「{options.Owner}」。" +
                              "只要该账号对仓库有写权限就能推送，否则会 403。");
            }

            if (string.IsNullOrWhiteSpace(options.Owner) || string.IsNullOrWhiteSpace(options.Repo))
            {
                AppendWarning("填的是自定义远程地址，跳过仓库检测。");
                return;
            }

            AppendSystem($"$ 查询仓库 {options.Owner}/{options.Repo}");
            var status = await api.GetRepoStatusAsync(options.Owner, options.Repo);

            if (!status.Exists)
            {
                AppendWarning($"仓库 {options.Owner}/{options.Repo} 不存在，或当前 Token 看不到它。");

                bool create = DebugMode;
                if (!create)
                {
                    var answer = MessageBox.Show(Window.GetWindow(this),
                        $"仓库 {options.Owner}/{options.Repo} 不存在。\n\n" +
                        "要现在创建一个 public 仓库吗？\n" +
                        "（GitHub Pages 免费账号只能用于 public 仓库）",
                        "创建仓库", MessageBoxButton.YesNo, MessageBoxImage.Question,
                        MessageBoxResult.Yes);

                    create = answer == MessageBoxResult.Yes;
                }

                if (!create)
                {
                    finalStatus = "仓库不存在";
                    return;
                }

                await api.CreateRepoAsync(options.Repo, isPrivate: false);
                AppendSuccess($"已创建 public 仓库 {options.Owner}/{options.Repo}。");
                status = await api.GetRepoStatusAsync(options.Owner, options.Repo);
            }

            AppendSuccess($"仓库可用：{status.FullName}" +
                          (status.IsPrivate ? "（私有仓库，免费账号无法开启 Pages）" : "（public）"));

            if (!string.IsNullOrWhiteSpace(status.DefaultBranch))
                AppendInfo($"默认分支：{status.DefaultBranch}");

            if (status.PagesEnabled)
            {
                AppendInfo($"Pages 已开启，源分支：{(status.PagesBranch.Length > 0 ? status.PagesBranch : "（未知）")}");
                if (!string.IsNullOrWhiteSpace(status.PagesUrl))
                    AppendInfo("当前地址：" + status.PagesUrl);
            }
            else
            {
                AppendInfo("Pages 还没开启，发布时会自动帮你打开。");
            }

            finalStatus = "仓库检测通过";
        }
        catch (GitHubApiException ex)
        {
            AppendError(ex.Message);
            finalStatus = "检测失败";
        }
        catch (Exception ex)
        {
            AppendError("检测时出错：" + ex.Message);
            finalStatus = "检测失败";
        }
        finally
        {
            SetBusy(false, finalStatus);
        }
    }

    // ==================================================================
    // 一键发布
    // ==================================================================

    private async void BtnPublish_Click(object sender, RoutedEventArgs e) => await PublishAsync();

    private async Task PublishAsync()
    {
        if (_busy) return;

        var options = CollectOptions();

        if (!Validate(options, out string error))
        {
            AppendError(error);
            return;
        }

        var site = BlogContext.Instance.CurrentSite ?? await BlogContext.Instance.EnsureSiteAsync();
        if (site is null)
        {
            AppendError("还没有连接博客目录，请先到「站点与目录」里选择一个 Hexo 博客。");
            return;
        }

        options.BlogRoot = site.RootPath;

        SaveSettings();
        HideResult();
        SetBusy(true, "发布中…");

        string finalStatus = "发布完成";

        // 每次发布用新的取消令牌，避免上一次取消后一直处于已取消状态
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();

        try
        {
            var result = await _publisher.PublishAsync(options, _runCts.Token);

            if (result.Success)
            {
                _siteUrl = result.SiteUrl ?? string.Empty;

                if (_siteUrl.Length > 0)
                {
                    ShowResult("发布成功，站点地址", _siteUrl);
                    _saved.LastPublishedUrl = _siteUrl;
                }
                else
                {
                    ShowResult("发布成功", "（自定义远程地址，未生成 Pages 地址）");
                }

                _saved.LastPublishedAt = DateTimeOffset.Now;
                SaveSettings();

                if (result.CommitShort is not null)
                    AppendSuccess($"提交：{result.CommitShort}" +
                                  (result.FileCount > 0 ? $"，共 {result.FileCount} 个文件变更" : string.Empty));

                finalStatus = "上次发布：" + DateTime.Now.ToString("MM-dd HH:mm");
            }
            else
            {
                AppendError(result.Message);
                finalStatus = "发布失败";
            }
        }
        catch (Exception ex)
        {
            AppendError("发布过程中出错：" + ex.Message);
            finalStatus = "发布失败";
        }
        finally
        {
            SetBusy(false, finalStatus);
        }
    }

    private void BtnStopPublish_Click(object sender, RoutedEventArgs e)
    {
        AppendSystem("正在停止…");
        _publisher.Stop();
        _runCts?.Cancel();
    }

    // ==================================================================
    // 结果卡
    // ==================================================================

    private void ShowResult(string title, string url)
    {
        _siteUrl = url;
        TxtResultTitle.Text = title;
        TxtSiteUrl.Text = url;
        TxtSiteUrl.ToolTip = url;
        PanelResult.Visibility = Visibility.Visible;
        BtnOpenSite.IsEnabled = !string.IsNullOrWhiteSpace(url);
        BtnCopyUrl.IsEnabled = !string.IsNullOrWhiteSpace(url);
    }

    private void HideResult() => PanelResult.Visibility = Visibility.Collapsed;

    private void BtnOpenSite_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_siteUrl)) return;

        try
        {
            Process.Start(new ProcessStartInfo(_siteUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendError("打开浏览器失败：" + ex.Message);
        }
    }

    private void BtnCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_siteUrl)) return;

        try
        {
            Clipboard.SetText(_siteUrl);
            AppendSystem("已复制站点地址。");
        }
        catch (Exception ex)
        {
            AppendError("复制失败：" + ex.Message);
        }
    }

    private void BtnGetToken_Click(object sender, RoutedEventArgs e)
    {
        const string url = "https://github.com/settings/tokens/new?scopes=repo&description=HexoBlogManager";

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendError("打开浏览器失败：" + ex.Message +
                        "\n可以手动访问：https://github.com/settings/tokens");
        }
    }

    // ==================================================================
    // 状态
    // ==================================================================

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;

        BtnPublish.IsEnabled = !busy;
        BtnCheck.IsEnabled = !busy;
        BtnStopPublish.IsEnabled = busy;

        // 执行期间锁住输入，避免中途改参数导致日志前后对不上
        TxtRepo.IsEnabled = !busy;
        TxtBranch.IsEnabled = !busy;
        TxtCommit.IsEnabled = !busy;
        PwdToken.IsEnabled = !busy;
        TxtProxy.IsEnabled = !busy;
        TxtRemote.IsEnabled = !busy;
        ChkClean.IsEnabled = !busy;
        ChkNoJekyll.IsEnabled = !busy;
        ChkAutoPages.IsEnabled = !busy;

        if (busy)
        {
            StatusDot.Fill = BrushGreen;
            TxtStatus.Text = status ?? "处理中…";
            TxtStatus.Foreground = BrushGreen;
        }
        else
        {
            StatusDot.Fill = (Brush)FindResource("Brush.TextTertiary");
            TxtStatus.Foreground = (Brush)FindResource("Brush.TextSecondary");

            if (!string.IsNullOrWhiteSpace(status))
                TxtStatus.Text = status;
        }
    }

    // ==================================================================
    // 日志
    // ==================================================================

    private void AppendLog(LogLevel level, string text)
    {
        _logLines.Add((level, AnsiText.Strip(text)));

        Brush fallbackBrush = BrushFor(level);
        Color fallbackColor = (fallbackBrush as SolidColorBrush)?.Color ?? Colors.Gainsboro;

        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 18 };

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

        if (paragraph.Inlines.Count == 0)
            paragraph.Inlines.Add(new Run(" "));

        LogBox.Document.Blocks.Add(paragraph);
        TrimIfNeeded();

        AutoScrollToEnd();
    }

    /// <summary>
    /// 自动滚到底。
    /// 只调 ScrollToEnd 时最后一行常被底边裁掉半行（滚动范围按旧布局算的），
    /// 所以在布局完成后补滚一次。
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
    private void AppendInfo(string text) => AppendLog(LogLevel.Info, text);
    private void AppendSuccess(string text) => AppendLog(LogLevel.Success, text);
    private void AppendWarning(string text) => AppendLog(LogLevel.Warning, text);
    private void AppendError(string text) => AppendLog(LogLevel.Error, text);

    private void TrimIfNeeded()
    {
        if (_logLines.Count <= MaxLogLines) return;

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
            AppendSystem("日志是空的。");
            return;
        }

        try
        {
            var sb = new StringBuilder();
            foreach (var (_, text) in _logLines)
                sb.AppendLine(text);

            Clipboard.SetText(sb.ToString());
            AppendSystem($"已复制 {_logLines.Count} 行日志到剪贴板。");
        }
        catch (Exception ex)
        {
            AppendError("复制失败：" + ex.Message);
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Document.Blocks.Clear();
        _logLines.Clear();
        AppendSystem("日志已清空。");
    }
}
