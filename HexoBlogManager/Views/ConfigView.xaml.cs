using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HexoBlogManager.Models;
using HexoBlogManager.Services;

namespace HexoBlogManager.Views;

/// <summary>
/// 配置管理页。
///
/// 数据来源全部走 <see cref="BlogConfigScanner"/>：
/// 它会找出博客里所有可能生效的配置文件（主配置、环境覆盖配置、主题三处候选），
/// 按 Hexo 的真实优先级合并，并记录每个字段的值、来源文件和行号。
///
/// 本页只做三件事：
/// 1) 把字段按绑定表填进表单，缺的用默认值，读坏的用默认值并提示；
/// 2) 把「识别到了什么、每项来自哪里」讲清楚（顶部识别卡 + 详情）；
/// 3) 保存时只改用户动过的字段，其余字节原样保留。
///
/// 容错原则：识别失败绝不阻断页面 —— 最差情况是「全部字段用默认值 + 顶部红点提示」，
/// 用户仍然可以手动填、手动存。
/// </summary>
public partial class ConfigView : UserControl
{
    private enum FieldKind
    {
        Text,
        Bool,
        Mapping
    }

    /// <summary>一个「YAML 字段 → 界面控件」的绑定。</summary>
    private sealed class FieldBinding
    {
        /// <summary>唯一键（用 YAML 路径即可），也是「用户是否改过」的追踪键。</summary>
        public required string Key { get; init; }

        /// <summary>报告里显示的中文名。</summary>
        public required string Label { get; init; }

        /// <summary>YAML 路径。</summary>
        public required string Path { get; init; }

        /// <summary>属于主题配置还是站点配置。</summary>
        public required bool Theme { get; init; }

        public required FieldKind Kind { get; init; }

        /// <summary>从控件读当前值（统一成字符串）。</summary>
        public required Func<string> Read { get; init; }

        /// <summary>把字符串写回控件。</summary>
        public required Action<string> Write { get; init; }

        /// <summary>配置里没有这个字段时用的默认值（字符串形式）。</summary>
        public string Fallback { get; init; } = string.Empty;
    }

    /// <summary>一次填充的统计结果，用于顶部那行汇总。</summary>
    private readonly struct FillStats
    {
        public int Filled { get; init; }
        public int Missing { get; init; }
        public int Inherited { get; init; }
        public int Overridden { get; init; }
        public int Preserved { get; init; }
    }

    private readonly List<FieldBinding> _bindings = new();

    /// <summary>上次填充后每个字段的值，用来判断「用户是不是改过」。</summary>
    private readonly Dictionary<string, string> _baseline = new(StringComparer.Ordinal);

    /// <summary>值来自继承或覆盖的字段：用户没动过时，保存不写它，避免把默认值抄进配置文件。</summary>
    private readonly HashSet<string> _guardedKeys = new(StringComparer.Ordinal);

    private ConfigService? _service;
    private BlogConfigSnapshot? _snapshot;

    private string? _loadedRoot;
    private string? _mainPath;
    private string? _themePath;

    private string _mainContent = string.Empty;
    private string _themeContent = string.Empty;

    /// <summary>菜单编辑框加载时的原始文本，用于判断用户是否真的改过菜单。</summary>
    private string _originalMenuText = string.Empty;

    private bool _loading;       // 填充控件期间忽略「已修改」标记
    private bool _initialized;
    private bool _detailVisible;

    /// <summary>调试用：指定启动时选中第几个选项卡（0 = 站点基础，1 = 主题）。</summary>
    public static int StartupTabIndex { get; set; }

    /// <summary>调试用：加载完成后自动滚到底部（用于截取长页面的下半部分）。</summary>
    public static bool DebugScrollToEnd { get; set; }

    /// <summary>调试用：加载完成后自动展开识别详情。</summary>
    public static bool DebugShowDetail { get; set; }

    /// <summary>调试用：加载完成后自动执行一次保存（用来验证「没改就不写盘」）。</summary>
    public static bool DebugAutoSave { get; set; }

    public ConfigView()
    {
        InitializeComponent();

        BuildBindings();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => BlogContext.Instance.SiteConnected -= OnSiteConnected;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 视图会被主窗口缓存，切走再切回时 Loaded 会再次触发，
        // 所以每次进页面都重新订阅（先减后加，避免重复订阅）。
        BlogContext.Instance.SiteConnected -= OnSiteConnected;
        BlogContext.Instance.SiteConnected += OnSiteConnected;

        if (!_initialized)
        {
            _initialized = true;

            if (StartupTabIndex >= 0 && StartupTabIndex < ConfigTabs.Items.Count)
                ConfigTabs.SelectedIndex = StartupTabIndex;

            await LoadAsync(preserveUserEdits: false);
            return;
        }

        // 切回来的场景：这期间博客可能换过，补一次检查
        await CheckBlogChangedAsync();
    }

    /// <summary>
    /// 绑定表：新增一个可编辑字段时，只需要在这里加一行，
    /// 填充、报告、保存三处会自动一致，不会出现「界面上有但读不到」的情况。
    /// </summary>
    private void BuildBindings()
    {
        // ---------- 站点基础（_config.yml） ----------
        _bindings.Add(new FieldBinding
        {
            Key = "title", Label = "网站标题", Path = "title", Theme = false, Kind = FieldKind.Text,
            Read = () => TxtTitle.Text, Write = v => TxtTitle.Text = v
        });

        _bindings.Add(new FieldBinding
        {
            Key = "subtitle", Label = "副标题", Path = "subtitle", Theme = false, Kind = FieldKind.Text,
            Read = () => TxtSubtitle.Text, Write = v => TxtSubtitle.Text = v
        });

        _bindings.Add(new FieldBinding
        {
            Key = "description", Label = "站点描述", Path = "description", Theme = false, Kind = FieldKind.Text,
            Read = () => TxtDescription.Text, Write = v => TxtDescription.Text = v
        });

        _bindings.Add(new FieldBinding
        {
            Key = "author", Label = "作者", Path = "author", Theme = false, Kind = FieldKind.Text,
            Read = () => TxtAuthor.Text, Write = v => TxtAuthor.Text = v
        });

        _bindings.Add(new FieldBinding
        {
            Key = "keywords", Label = "关键词", Path = "keywords", Theme = false, Kind = FieldKind.Text,
            Read = () => TxtKeywords.Text, Write = v => TxtKeywords.Text = v
        });

        _bindings.Add(new FieldBinding
        {
            Key = "language", Label = "语言", Path = "language", Theme = false, Kind = FieldKind.Text,
            Read = () => TxtLanguage.Text, Write = v => TxtLanguage.Text = v
        });

        _bindings.Add(new FieldBinding
        {
            Key = "url", Label = "站点地址", Path = "url", Theme = false, Kind = FieldKind.Text,
            Read = () => TxtUrl.Text, Write = v => TxtUrl.Text = v
        });

        // ---------- 主题（Butterfly） ----------
        _bindings.Add(new FieldBinding
        {
            Key = "menu", Label = "导航菜单", Path = "menu", Theme = true, Kind = FieldKind.Mapping,
            Read = () => TxtMenu.Text, Write = v => TxtMenu.Text = v
        });

        _bindings.Add(new FieldBinding
        {
            Key = "nav.display_title", Label = "导航栏显示站点标题", Path = "nav.display_title",
            Theme = true, Kind = FieldKind.Bool, Fallback = "true",
            Read = () => BoolText(ChkNavDisplayTitle),
            Write = v => ChkNavDisplayTitle.IsChecked = ParseBool(v, true)
        });

        _bindings.Add(new FieldBinding
        {
            Key = "nav.fixed", Label = "导航栏固定顶部", Path = "nav.fixed",
            Theme = true, Kind = FieldKind.Bool, Fallback = "false",
            Read = () => BoolText(ChkNavFixed),
            Write = v => ChkNavFixed.IsChecked = ParseBool(v, false)
        });

        _bindings.Add(new FieldBinding
        {
            Key = "footer.custom_text", Label = "页脚文字", Path = "footer.custom_text",
            Theme = true, Kind = FieldKind.Text,
            Read = () => TxtFooterText.Text, Write = v => TxtFooterText.Text = v
        });

        _bindings.Add(new FieldBinding
        {
            Key = "footer.copyright.enable", Label = "显示版权信息", Path = "footer.copyright.enable",
            Theme = true, Kind = FieldKind.Bool, Fallback = "true",
            Read = () => BoolText(ChkCopyright),
            Write = v => ChkCopyright.IsChecked = ParseBool(v, true)
        });

        _bindings.Add(new FieldBinding
        {
            Key = "footer.owner.enable", Label = "显示作者链接", Path = "footer.owner.enable",
            Theme = true, Kind = FieldKind.Bool, Fallback = "true",
            Read = () => BoolText(ChkOwner),
            Write = v => ChkOwner.IsChecked = ParseBool(v, true)
        });

        _bindings.Add(new FieldBinding
        {
            Key = "comments.use", Label = "评论系统", Path = "comments.use",
            Theme = true, Kind = FieldKind.Text,
            Read = GetCommentSelection, Write = SetCommentSelection
        });

        _bindings.Add(new FieldBinding
        {
            Key = "giscus.repo", Label = "giscus 仓库", Path = "giscus.repo",
            Theme = true, Kind = FieldKind.Text,
            Read = () => TxtGiscusRepo.Text, Write = v => TxtGiscusRepo.Text = v
        });

        _bindings.Add(new FieldBinding
        {
            Key = "giscus.repo_id", Label = "giscus repo_id", Path = "giscus.repo_id",
            Theme = true, Kind = FieldKind.Text,
            Read = () => TxtGiscusRepoId.Text, Write = v => TxtGiscusRepoId.Text = v
        });

        _bindings.Add(new FieldBinding
        {
            Key = "giscus.category_id", Label = "giscus category_id", Path = "giscus.category_id",
            Theme = true, Kind = FieldKind.Text,
            Read = () => TxtGiscusCategoryId.Text, Write = v => TxtGiscusCategoryId.Text = v
        });
    }

    // ==================================================================
    // 加载与识别
    // ==================================================================

    /// <param name="preserveUserEdits">
    /// true 时保留用户改过但没保存的字段（「重新识别」用），
    /// false 时以磁盘内容为准全部覆盖（首次加载、切换博客用）。
    /// </param>
    private async Task LoadAsync(bool preserveUserEdits)
    {
        try
        {
            var site = BlogContext.Instance.CurrentSite ?? await BlogContext.Instance.EnsureSiteAsync();
            if (site is null)
            {
                SetEditable(false);
                SetDetectState(DetectState.Failed, "还没有连接博客目录", "请先在「站点与目录」里选择一个 Hexo 博客。");
                SetStatus("还没有连接博客目录。请先在「站点与目录」里选择一个 Hexo 博客。", isWarning: true);
                return;
            }

            SetEditable(false);
            SetStatus("正在识别配置…");
            SetDetectState(DetectState.Working, "正在识别已有配置…", string.Empty);

            // 扫描放到后台线程：要读好几个文件、还要解析，别卡住 UI
            var snapshot = await Task.Run(() => BlogConfigScanner.Scan(site.RootPath));

            ApplySnapshot(site, snapshot, preserveUserEdits);
        }
        catch (Exception ex)
        {
            // 任何意外都不能让页面变砖：保持可编辑，字段留在默认值
            SetEditable(true);
            SetDetectState(DetectState.Failed, "配置识别失败，已使用默认值", ex.Message);
            SetStatus("加载配置失败：" + ex.Message, isWarning: true);
        }
    }

    private void ApplySnapshot(BlogSiteInfo site, BlogConfigSnapshot snapshot, bool preserveUserEdits)
    {
        _snapshot = snapshot;
        _loadedRoot = site.RootPath;
        _service = new ConfigService(site.RootPath);

        _mainPath = snapshot.MainWritePath;
        _themePath = snapshot.ThemeWritePath;
        _mainContent = snapshot.MainContent;
        _themeContent = snapshot.ThemeContent;

        // 主题区块：没有任何主题配置时给出说明，而不是显示一堆空控件
        bool hasTheme = _themePath is not null && _themeContent.Length > 0;
        PanelTheme.Visibility = hasTheme ? Visibility.Visible : Visibility.Collapsed;
        PanelNoTheme.Visibility = hasTheme ? Visibility.Collapsed : Visibility.Visible;

        FillStats stats = FillFields(snapshot, preserveUserEdits);

        TxtMainPath.Text = BuildMainPathText(snapshot);
        TxtThemePath.Text = _themePath is null
            ? $"主题配置：未找到（主题：{(snapshot.ThemeName.Length > 0 ? snapshot.ThemeName : "未知")}）"
            : "文件：" + _themePath + "\n" + _service.DescribeThemeConfig(_themePath);

        UpdateDetectionPanel(snapshot, stats);

        // ---------- 可编辑性 ----------
        // 配置文件读不出来时禁止保存：此时表单里是默认值，写回去会把用户的配置覆盖成一片空白
        bool readBroken = snapshot.MainUnreadable || snapshot.ThemeUnreadable;
        bool canEdit = snapshot.MainExists && !readBroken;

        SetEditable(canEdit);

        if (snapshot.Error is not null)
            SetStatus("配置识别失败，已使用默认值：" + snapshot.Error, isWarning: true);
        else if (readBroken)
            SetStatus("配置文件编码/权限异常，读取结果不可信，已禁用保存以保护原文件。", isWarning: true);
        else if (!snapshot.MainExists)
            SetStatus($"_config.yml 不存在：{snapshot.MainWritePath ?? site.RootPath}", isWarning: true);
        else
            SetStatus(BuildStatusText(snapshot) + " · 修改后点右下角保存");

        if (DebugShowDetail && !_detailVisible)
            ToggleDetail(show: true);

        if (DebugScrollToEnd)
        {
            // 等布局完成后再滚，否则滚动距离按旧高度算
            _ = Dispatcher.BeginInvoke(() => ThemeScroll.ScrollToEnd(),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (DebugAutoSave)
        {
            _ = Dispatcher.BeginInvoke(() => Save(),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>按绑定表把配置值填进表单。</summary>
    private FillStats FillFields(BlogConfigSnapshot snapshot, bool preserveUserEdits)
    {
        int filled = 0, missing = 0, inherited = 0, overridden = 0, preserved = 0;

        _loading = true;
        _guardedKeys.Clear();

        try
        {
            foreach (var binding in _bindings)
            {
                string current = binding.Read();

                // 用户改过且要求保留 → 原样不动，连 baseline 都不更新
                bool userEdited = preserveUserEdits
                                  && _baseline.TryGetValue(binding.Key, out string? old)
                                  && !string.Equals(current, old, StringComparison.Ordinal);

                if (userEdited)
                {
                    preserved++;
                    continue;
                }

                // 菜单比较特殊：只认写入目标里的内容，不继承主题默认菜单
                if (binding.Kind == FieldKind.Mapping)
                {
                    bool hasKey = snapshot.ThemeHasKey(binding.Path);

                    if (hasKey)
                    {
                        binding.Write(FormatMenu(snapshot.GetMenu()));
                        filled++;
                    }
                    else
                    {
                        binding.Write(string.Empty);
                        missing++;
                    }

                    _originalMenuText = binding.Read();
                    _baseline[binding.Key] = _originalMenuText;
                    continue;
                }

                var resolved = binding.Theme
                    ? snapshot.ResolveTheme(binding.Path)
                    : snapshot.ResolveMain(binding.Path);

                string text = resolved.HasValue ? resolved.Text : binding.Fallback;
                binding.Write(text);
                _baseline[binding.Key] = binding.Read();

                switch (resolved.State)
                {
                    case ConfigFieldState.Found:
                        filled++;
                        break;

                    case ConfigFieldState.Inherited:
                        inherited++;
                        _guardedKeys.Add(binding.Key);
                        break;

                    case ConfigFieldState.Overridden:
                        overridden++;
                        _guardedKeys.Add(binding.Key);
                        break;

                    default:   // Missing / Invalid
                        missing++;
                        break;
                }
            }
        }
        finally
        {
            _loading = false;
        }

        UpdateGiscusPanel();

        return new FillStats
        {
            Filled = filled,
            Missing = missing,
            Inherited = inherited,
            Overridden = overridden,
            Preserved = preserved
        };
    }

    /// <summary>切换博客时自动重新识别（避免页面一直显示上一个博客的配置）。</summary>
    private async void OnSiteConnected(object? sender, BlogSiteInfo site)
    {
        if (!_initialized || _loading) return;
        await ConfirmAndReloadAsync(site.RootPath);
    }

    /// <summary>页面被切回来时检查一下博客是否变过。</summary>
    private async Task CheckBlogChangedAsync()
    {
        if (_loading) return;

        string? root = BlogContext.Instance.CurrentBlogPath;
        if (string.IsNullOrWhiteSpace(root))
            return;

        await ConfirmAndReloadAsync(root);
    }

    /// <summary>博客换了就重载配置；有未保存修改时先问一次。</summary>
    private async Task ConfirmAndReloadAsync(string newRoot)
    {
        if (_loadedRoot is not null &&
            string.Equals(_loadedRoot, newRoot, StringComparison.OrdinalIgnoreCase))
            return;

        if (HasUnsavedChanges())
        {
            string message = $"博客已切换到「{newRoot}」。\n\n" +
                             "重新载入配置会丢弃当前未保存的修改，是否继续？";

            var owner = Window.GetWindow(this);
            var result = owner is null
                ? MessageBox.Show(message, "切换博客", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No)
                : MessageBox.Show(owner, message, "切换博客", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                SetStatus("当前显示的还是上一个博客的配置，点「重新加载」可切换到新博客。", isWarning: true);
                return;
            }
        }

        await LoadAsync(preserveUserEdits: false);
    }

    // ==================================================================
    // 保存
    // ==================================================================

    private void BtnSave_Click(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        if (_service is null || _snapshot is null)
        {
            SetStatus("还没有加载配置，无法保存。", isWarning: true);
            return;
        }

        if (_mainPath is null)
        {
            SetStatus("没有找到 _config.yml，无法保存。", isWarning: true);
            return;
        }

        var backups = new List<string>();

        try
        {
            // ---------- 主配置 ----------
            string mainNew = _mainContent;

            foreach (var binding in _bindings)
            {
                if (binding.Theme || binding.Kind == FieldKind.Mapping)
                    continue;

                mainNew = ApplyField(mainNew, binding);
            }

            if (!string.Equals(mainNew, _mainContent, StringComparison.Ordinal))
            {
                backups.Add(_service.Backup(_mainPath));
                _service.Save(_mainPath, mainNew);
                _service.CleanOldBackups(_mainPath);
                _mainContent = mainNew;
            }

            // ---------- 主题配置 ----------
            if (_themePath is not null)
            {
                string themeNew = _themeContent;

                foreach (var binding in _bindings)
                {
                    if (!binding.Theme || binding.Kind == FieldKind.Mapping)
                        continue;

                    themeNew = ApplyField(themeNew, binding);
                }

                // 菜单只有真的改过才重写整块，避免无谓地抹掉块内注释
                var menuBinding = _bindings.FirstOrDefault(b => b.Kind == FieldKind.Mapping);

                if (menuBinding is not null &&
                    !string.Equals(menuBinding.Read(), _originalMenuText, StringComparison.Ordinal))
                {
                    themeNew = YamlPathEditor.SetMapping(themeNew, menuBinding.Path,
                        ParseMenu(menuBinding.Read()));
                }

                if (!string.Equals(themeNew, _themeContent, StringComparison.Ordinal))
                {
                    backups.Add(_service.Backup(_themePath));
                    _service.Save(_themePath, themeNew);
                    _service.CleanOldBackups(_themePath);
                    _themeContent = themeNew;
                    _originalMenuText = menuBinding?.Read() ?? string.Empty;
                }
            }

            if (backups.Count == 0)
            {
                SetStatus("没有检测到实际修改，未写入任何文件。");
                return;
            }

            // 保存成功后，控件里的值就是磁盘上的值，刷新 baseline 让「未保存」判断回到干净状态
            foreach (var binding in _bindings)
                _baseline[binding.Key] = binding.Read();

            string names = string.Join("、", backups.Select(Path.GetFileName));
            SetStatus($"已保存。原文件已备份为：{names}（最多保留 10 份备份）");

            // 重新识别一次，让顶部识别卡反映最新的行号与状态
            _ = RefreshSnapshotQuietlyAsync();
        }
        catch (UnauthorizedAccessException ex)
        {
            SetStatus($"没有写入权限，保存失败：{ex.Message}", isWarning: true);
        }
        catch (IOException ex)
        {
            SetStatus($"写入失败：{ex.Message}\n（文件可能正被编辑器占用，关闭后重试）", isWarning: true);
        }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + ex.Message, isWarning: true);
        }
    }

    /// <summary>
    /// 把控件里的值写进文本。
    ///
    /// 三条保护：
    /// 1) 值来自继承/覆盖且用户没动过 → 一个字节都不碰（否则会把主题默认值抄进用户配置）；
    /// 2) 配置里本来没有这个键、值又是空的 → 不新建无意义的空键；
    /// 3) 配置里没有这个键、值等于默认值 → 也不新建。
    /// </summary>
    private string ApplyField(string content, FieldBinding binding)
    {
        string value = binding.Read().Trim();

        bool untouchedGuarded = _guardedKeys.Contains(binding.Key)
                                && _baseline.TryGetValue(binding.Key, out string? baseline)
                                && string.Equals(value, baseline, StringComparison.Ordinal);

        if (untouchedGuarded)
            return content;

        bool exists = YamlPathEditor.Exists(content, binding.Path);

        if (!exists)
        {
            if (value.Length == 0)
                return content;

            if (string.Equals(value, binding.Fallback, StringComparison.Ordinal))
                return content;
        }

        // 布尔值不能加引号，否则 fixed: "true" 在 YAML 里会变成字符串
        return YamlPathEditor.SetScalar(content, binding.Path, value,
            raw: binding.Kind == FieldKind.Bool);
    }

    /// <summary>保存后静默刷新识别结果（不动表单值，只更新报告与行号）。</summary>
    private async Task RefreshSnapshotQuietlyAsync()
    {
        try
        {
            string? root = _loadedRoot;
            if (root is null) return;

            var snapshot = await Task.Run(() => BlogConfigScanner.Scan(root));

            _snapshot = snapshot;
            _mainPath = snapshot.MainWritePath;
            _themePath = snapshot.ThemeWritePath;
            _mainContent = snapshot.MainContent;
            _themeContent = snapshot.ThemeContent;

            UpdateDetectionPanel(snapshot, new FillStats());
        }
        catch
        {
            // 刷新报告失败不影响保存结果
        }
    }

    private async void BtnRescan_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync(preserveUserEdits: true);
    }

    private async void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        if (HasUnsavedChanges())
        {
            var owner = Window.GetWindow(this);
            const string message = "重新加载会丢弃当前未保存的修改，确定继续吗？";

            var result = owner is null
                ? MessageBox.Show(message, "重新加载", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No)
                : MessageBox.Show(owner, message, "重新加载", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
                return;
        }

        await LoadAsync(preserveUserEdits: false);
    }

    // ==================================================================
    // 识别结果展示
    // ==================================================================

    private enum DetectState
    {
        Working,
        Ok,
        Warning,
        Failed
    }

    private void SetDetectState(DetectState state, string summary, string detail)
    {
        TxtDetectSummary.Text = summary;
        TxtDetectSummary.Foreground = (Brush)FindResource(
            state == DetectState.Failed ? "Brush.Danger" : "Brush.TextSecondary");

        DetectDot.Fill = state switch
        {
            DetectState.Ok => (Brush)FindResource("Brush.Success"),
            DetectState.Warning => new SolidColorBrush(Color.FromRgb(0xB2, 0x6B, 0x00)),
            DetectState.Failed => (Brush)FindResource("Brush.Danger"),
            _ => (Brush)FindResource("Brush.TextTertiary")
        };

        if (detail.Length > 0)
            TxtDetailText.Text = detail;
    }

    private void UpdateDetectionPanel(BlogConfigSnapshot snapshot, FillStats stats)
    {
        bool hasError = snapshot.Error is not null;
        bool hasWarning = snapshot.Warnings.Count > 0
                          || snapshot.Files.Any(f => f.Error is not null || f.Warnings.Count > 0);

        var summary = new StringBuilder();

        if (hasError)
        {
            summary.Append("配置识别失败，已使用默认值：").Append(snapshot.Error);
        }
        else
        {
            int fileCount = snapshot.Files.Count(f => f.Exists && f.Parsed);
            summary.Append($"已识别 {fileCount} 个配置文件");

            int touched = stats.Filled + stats.Inherited + stats.Overridden;
            if (touched + stats.Missing + stats.Preserved > 0)
            {
                summary.Append($" · 表单填入 {touched} 项");
                if (stats.Inherited > 0)
                    summary.Append($"（{stats.Inherited} 项沿用主题默认）");
                if (stats.Missing > 0)
                    summary.Append($" · {stats.Missing} 项未设置，使用默认值");
                if (stats.Overridden > 0)
                    summary.Append($" · {stats.Overridden} 项被覆盖配置改写");
                if (stats.Preserved > 0)
                    summary.Append($" · 已保留 {stats.Preserved} 项你的修改");
            }
        }

        SetDetectState(
            hasError ? DetectState.Failed : hasWarning ? DetectState.Warning : DetectState.Ok,
            summary.ToString(),
            BuildDetailReport(snapshot));
    }

    /// <summary>生成展开后的详情文本：文件清单 + 字段来源 + 提示。</summary>
    private string BuildDetailReport(BlogConfigSnapshot snapshot)
    {
        var sb = new StringBuilder();

        // ---------- 文件 ----------
        sb.AppendLine("【配置文件】");

        if (snapshot.Files.Count == 0)
        {
            sb.AppendLine("  （没有找到任何配置文件）");
        }

        foreach (var file in snapshot.Files.Where(f => f.Exists))
        {
            string relative = ToRelative(snapshot.BlogRoot, file.Path);
            string status;

            if (file.Error is not null)
                status = "读取失败：" + file.Error;
            else if (!file.Parsed)
                status = "未解析";
            else
                status = $"{file.EntryCount} 项";

            string mark = file.Parsed ? "[已读取]" : "[失败]";

            sb.Append("  ").Append(mark).Append(' ').Append(relative)
              .Append("  · ").Append(file.RoleText);

            if (file.IsWriteTarget)
                sb.Append(" · 写入目标");

            sb.Append(" · ").Append(status);

            if (file.EncodingNote is not null)
                sb.Append(" · ").Append(file.EncodingNote);

            sb.AppendLine();
        }

        // 候选位置里不存在的那些折成一行，避免详情被一堆「不存在」淹没
        var missingFiles = snapshot.Files.Where(f => !f.Exists).ToList();

        if (missingFiles.Count > 0)
        {
            sb.Append("  [不存在] 其余 ").Append(missingFiles.Count).Append(" 处候选位置：")
              .AppendLine(string.Join("、", missingFiles.Select(f => ToRelative(snapshot.BlogRoot, f.Path))));
        }

        // ---------- 字段 ----------
        sb.AppendLine();
        sb.AppendLine("【字段来源】");

        foreach (var binding in _bindings)
        {
            string line = binding.Kind == FieldKind.Mapping
                ? DescribeMenuField(snapshot, binding)
                : DescribeField(snapshot, binding);

            sb.Append("  · ").Append(binding.Label).Append("  ").Append(binding.Path).Append("  ").AppendLine(line);
        }

        // ---------- 提示 ----------
        var notes = new List<string>();
        notes.AddRange(snapshot.Warnings);

        foreach (var file in snapshot.Files)
        {
            foreach (string warning in file.Warnings.Take(5))
                notes.Add($"{ToRelative(snapshot.BlogRoot, file.Path)}：{warning}");
        }

        if (notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【提示】");

            foreach (string note in notes)
                sb.Append("  · ").AppendLine(note);
        }

        sb.AppendLine();
        sb.Append("识别时间：").Append(snapshot.ScannedAt.ToString("HH:mm:ss"))
          .Append("　主题：").Append(snapshot.ThemeName.Length > 0 ? snapshot.ThemeName : "未知");

        return sb.ToString();
    }

    private string DescribeField(BlogConfigSnapshot snapshot, FieldBinding binding)
    {
        var resolved = binding.Theme
            ? snapshot.ResolveTheme(binding.Path)
            : snapshot.ResolveMain(binding.Path);

        switch (resolved.State)
        {
            case ConfigFieldState.Found:
            case ConfigFieldState.Inherited:
            case ConfigFieldState.Overridden:
                {
                    string value = Shorten(resolved.Text);
                    string source = resolved.SourceLine > 0
                        ? $"{Path.GetFileName(resolved.SourceFile)} 第 {resolved.SourceLine} 行"
                        : Path.GetFileName(resolved.SourceFile);

                    string suffix = resolved.Note.Length > 0 ? $"（{resolved.Note}）" : string.Empty;
                    return $"= {value}　← {source}{suffix}";
                }

            case ConfigFieldState.Invalid:
                return "读取失败，已使用默认值";

            default:
                return $"未设置，使用默认值{(binding.Fallback.Length > 0 ? $"「{binding.Fallback}」" : "（空）")}";
        }
    }

    private static string DescribeMenuField(BlogConfigSnapshot snapshot, FieldBinding binding)
    {
        var menu = snapshot.GetMenu();

        return menu.Count > 0
            ? $"= {menu.Count} 项（{Path.GetFileName(snapshot.ThemeWritePath ?? string.Empty)}）"
            : "未设置，菜单为空";
    }

    private void BtnDetail_Click(object sender, RoutedEventArgs e) => ToggleDetail(!_detailVisible);

    private void ToggleDetail(bool show)
    {
        _detailVisible = show;
        PanelDetail.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BtnDetail.Content = show ? "收起" : "详情";
    }

    private string BuildMainPathText(BlogConfigSnapshot snapshot)
    {
        if (snapshot.MainWritePath is null)
            return "文件：未找到 _config.yml";

        var alternates = snapshot.Files
            .Where(f => f.Role == ConfigRole.Alternate && f.Exists)
            .Select(f => f.FileName)
            .ToList();

        string text = "文件：" + snapshot.MainWritePath;

        if (alternates.Count > 0)
            text += "\n另有覆盖配置：" + string.Join("、", alternates) + "（优先级更高，会覆盖上面的值）";

        return text;
    }

    private static string BuildStatusText(BlogConfigSnapshot snapshot)
    {
        string main = snapshot.MainWritePath is null ? "_config.yml" : Path.GetFileName(snapshot.MainWritePath);
        string theme = snapshot.ThemeWritePath is null
            ? "未找到主题配置"
            : Path.GetFileName(snapshot.ThemeWritePath);

        return $"已加载 {main} 与 {theme}";
    }

    /// <summary>把绝对路径转成相对博客根目录的短路径，报告里更好读。</summary>
    private static string ToRelative(string root, string path)
    {
        try
        {
            string relative = Path.GetRelativePath(root, path);
            return relative.Length > 0 && relative.Length < path.Length ? relative : path;
        }
        catch
        {
            return path;
        }
    }

    private static string Shorten(string text, int max = 46)
    {
        string single = text.Replace("\r", " ").Replace("\n", " ");
        return single.Length <= max ? single : single[..max] + "…";
    }

    // ==================================================================
    // 评论系统下拉框
    // ==================================================================

    /// <summary>当前选中的评论系统值（写回配置用）。</summary>
    private string GetCommentSelection()
        => (CmbComments.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    /// <summary>
    /// 把配置里的值映射到下拉框。
    /// 遇到本工具不认识的评论系统（Valine、Waline……）时插入一个「保持原样」的选项，
    /// 这样保存时不会把用户原来的评论配置抹成空。
    /// </summary>
    private void SetCommentSelection(string value)
    {
        string raw = (value ?? string.Empty).Trim();

        // 先清掉上一次插入的临时项（索引 2 之后）
        while (CmbComments.Items.Count > 2)
            CmbComments.Items.RemoveAt(CmbComments.Items.Count - 1);

        if (raw.Length == 0)
        {
            CmbComments.SelectedIndex = 0;
            return;
        }

        if (raw.Equals("Giscus", StringComparison.OrdinalIgnoreCase))
        {
            CmbComments.SelectedIndex = 1;
            return;
        }

        var item = new ComboBoxItem
        {
            Content = $"{raw}（其他评论系统，保持原样）",
            Tag = raw
        };

        CmbComments.Items.Add(item);
        CmbComments.SelectedIndex = CmbComments.Items.Count - 1;
    }

    private void CmbComments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateGiscusPanel();

        if (_loading) return;
        MarkDirty();
    }

    /// <summary>只有选了 Giscus 才展开参数区，界面不至于一直堆着用不上的字段。</summary>
    private void UpdateGiscusPanel()
    {
        if (PanelGiscus is null) return;

        string? tag = (CmbComments.SelectedItem as ComboBoxItem)?.Tag as string;
        PanelGiscus.Visibility = tag == "Giscus" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================================================================
    // 工具
    // ==================================================================

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        MarkDirty();
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        MarkDirty();
    }

    private void MarkDirty()
    {
        SetStatus("有未保存的修改 · 点右下角「保存修改」写入文件");
    }

    /// <summary>是否有「控件值和上次填充的值不一致」的字段。</summary>
    private bool HasUnsavedChanges()
    {
        foreach (var binding in _bindings)
        {
            if (_baseline.TryGetValue(binding.Key, out string? value) &&
                !string.Equals(value, binding.Read(), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private void SetEditable(bool editable)
    {
        BtnSave.IsEnabled = editable;
        BtnReload.IsEnabled = editable;
    }

    private void SetStatus(string text, bool isWarning = false)
    {
        TxtStatus.Text = text;
        TxtStatus.Foreground = isWarning
            ? (Brush)FindResource("Brush.Danger")
            : (Brush)FindResource("Brush.TextTertiary");
    }

    private static string BoolText(CheckBox checkBox) => checkBox.IsChecked == true ? "true" : "false";

    /// <summary>把配置里的 true/false/yes/no/1/0 转成布尔，认不出来用默认值。</summary>
    private static bool ParseBool(string? text, bool fallback)
    {
        return (text ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => fallback
        };
    }

    /// <summary>把菜单映射格式化成一行一条，方便编辑。</summary>
    private static string FormatMenu(List<(string Key, string Value)> menu)
        => string.Join("\n", menu.Select(item => $"{item.Key}: {item.Value}"));

    /// <summary>把「一行一条」的文本解析回键值对，忽略空行和注释行。</summary>
    private static List<(string Key, string Value)> ParseMenu(string text)
    {
        var result = new List<(string, string)>();

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();

            if (key.Length == 0 || value.Length == 0)
                continue;

            result.Add((key, value));
        }

        return result;
    }
}
