using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HexoBlogManager.Services;

namespace HexoBlogManager.Views;

/// <summary>
/// 配置管理页。
///
/// 两个配置文件：
/// · 站点基础 → &lt;blog&gt;/_config.yml
/// · Butterfly 主题 → _config.butterfly.yml / themes/butterfly/_config.yml / node_modules 下的默认配置
///
/// 保存策略（重点）：
/// 业务上只修改用户点过的那几项，其余行（含注释、字段顺序、缩进）原样保留。
/// 写盘前先把原文件复制成 .bak 备份，最多保留 10 份。
/// 只有内容真正变化时才写文件，避免无意义地刷新修改时间。
/// </summary>
public partial class ConfigView : UserControl
{
    private ConfigService? _service;

    private string? _mainPath;
    private string? _themePath;

    private string _mainContent = string.Empty;
    private string _themeContent = string.Empty;

    /// <summary>菜单编辑框加载时的原始文本，用于判断用户是否真的改过菜单。</summary>
    private string _originalMenuText = string.Empty;

    private bool _loading;       // 填充控件期间忽略「已修改」标记
    private bool _initialized;

    /// <summary>调试用：指定启动时选中第几个选项卡（0 = 站点基础，1 = 主题）。</summary>
    public static int StartupTabIndex { get; set; }

    /// <summary>调试用：加载完成后自动滚到底部（用于截取长页面的下半部分）。</summary>
    public static bool DebugScrollToEnd { get; set; }

    public ConfigView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        if (StartupTabIndex >= 0 && StartupTabIndex < ConfigTabs.Items.Count)
            ConfigTabs.SelectedIndex = StartupTabIndex;

        await LoadAsync();
    }

    // ==================================================================
    // 加载
    // ==================================================================

    private async Task LoadAsync()
    {
        try
        {
            var site = BlogContext.Instance.CurrentSite ?? await BlogContext.Instance.EnsureSiteAsync();
            if (site is null)
            {
                SetEditable(false);
                SetStatus("还没有连接博客目录。请先在「站点与目录」里选择一个 Hexo 博客。", isWarning: true);
                return;
            }

            SetEditable(false);
            SetStatus("正在加载配置…");

            _service = new ConfigService(site.RootPath);
            _mainPath = _service.MainConfigPath;
            _themePath = _service.FindThemeConfig();

            if (!File.Exists(_mainPath))
            {
                SetStatus($"_config.yml 不存在：{_mainPath}", isWarning: true);
                return;
            }

            _mainContent = await Task.Run(() => _service.Read(_mainPath));

            _themeContent = _themePath is not null
                ? await Task.Run(() => _service.Read(_themePath))
                : string.Empty;

            FillMainFields();
            FillThemeFields();

            SetEditable(true);

            string summary = _themePath is null
                ? $"已加载 {Path.GetFileName(_mainPath)}（未找到 Butterfly 主题配置）"
                : $"已加载 {Path.GetFileName(_mainPath)} 与 {Path.GetFileName(_themePath)}";

            SetStatus(summary + " · 修改后点右下角保存");

            if (DebugScrollToEnd)
            {
                // 等布局完成后再滚，否则滚动距离按旧高度算
                _ = Dispatcher.BeginInvoke(() => ThemeScroll.ScrollToEnd(),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
        catch (Exception ex)
        {
            SetEditable(false);
            SetStatus("加载配置失败：" + ex.Message, isWarning: true);
        }
    }

    private void FillMainFields()
    {
        _loading = true;

        TxtTitle.Text = YamlPathEditor.GetScalar(_mainContent, "title");
        TxtSubtitle.Text = YamlPathEditor.GetScalar(_mainContent, "subtitle");
        TxtDescription.Text = YamlPathEditor.GetScalar(_mainContent, "description");
        TxtAuthor.Text = YamlPathEditor.GetScalar(_mainContent, "author");
        TxtKeywords.Text = YamlPathEditor.GetScalar(_mainContent, "keywords");
        TxtLanguage.Text = YamlPathEditor.GetScalar(_mainContent, "language");
        TxtUrl.Text = YamlPathEditor.GetScalar(_mainContent, "url");

        TxtMainPath.Text = "文件：" + _mainPath;

        _loading = false;
    }

    private void FillThemeFields()
    {
        if (_themePath is null || _service is null)
        {
            PanelTheme.Visibility = Visibility.Collapsed;
            PanelNoTheme.Visibility = Visibility.Visible;
            return;
        }

        PanelTheme.Visibility = Visibility.Visible;
        PanelNoTheme.Visibility = Visibility.Collapsed;

        _loading = true;

        _originalMenuText = FormatMenu(YamlPathEditor.GetMapping(_themeContent, "menu"));
        TxtMenu.Text = _originalMenuText;

        ChkNavDisplayTitle.IsChecked = YamlPathEditor.GetBool(_themeContent, "nav.display_title", true);
        ChkNavFixed.IsChecked = YamlPathEditor.GetBool(_themeContent, "nav.fixed", false);

        TxtFooterText.Text = YamlPathEditor.GetScalar(_themeContent, "footer.custom_text");
        ChkCopyright.IsChecked = YamlPathEditor.GetBool(_themeContent, "footer.copyright.enable", true);
        ChkOwner.IsChecked = YamlPathEditor.GetBool(_themeContent, "footer.owner.enable", true);

        // ---------- 评论系统 ----------
        string commentSystem = YamlPathEditor.GetScalar(_themeContent, "comments.use").Trim();
        CmbComments.SelectedIndex = commentSystem.Equals("Giscus", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        TxtGiscusRepo.Text = YamlPathEditor.GetScalar(_themeContent, "giscus.repo");
        TxtGiscusRepoId.Text = YamlPathEditor.GetScalar(_themeContent, "giscus.repo_id");
        TxtGiscusCategoryId.Text = YamlPathEditor.GetScalar(_themeContent, "giscus.category_id");

        UpdateGiscusPanel();

        _loading = false;

        TxtThemePath.Text = "文件：" + _themePath + "\n" + _service.DescribeThemeConfig(_themePath);
    }

    // ==================================================================
    // 保存
    // ==================================================================

    private void BtnSave_Click(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        if (_service is null)
        {
            SetStatus("还没有加载配置，无法保存。", isWarning: true);
            return;
        }

        var backups = new List<string>();

        try
        {
            // ---------- 主配置 ----------
            string mainNew = _mainContent;
            mainNew = YamlPathEditor.SetScalar(mainNew, "title", TxtTitle.Text.Trim());
            mainNew = YamlPathEditor.SetScalar(mainNew, "subtitle", TxtSubtitle.Text.Trim());
            mainNew = YamlPathEditor.SetScalar(mainNew, "description", TxtDescription.Text.Trim());
            mainNew = YamlPathEditor.SetScalar(mainNew, "author", TxtAuthor.Text.Trim());
            mainNew = YamlPathEditor.SetScalar(mainNew, "keywords", TxtKeywords.Text.Trim());
            mainNew = YamlPathEditor.SetScalar(mainNew, "language", TxtLanguage.Text.Trim());
            mainNew = YamlPathEditor.SetScalar(mainNew, "url", TxtUrl.Text.Trim());

            if (!string.Equals(mainNew, _mainContent, StringComparison.Ordinal))
            {
                backups.Add(_service.Backup(_mainPath!));
                _service.Save(_mainPath!, mainNew);
                _service.CleanOldBackups(_mainPath!);
                _mainContent = mainNew;
            }

            // ---------- 主题配置 ----------
            if (_themePath is not null)
            {
                string themeNew = _themeContent;

                themeNew = YamlPathEditor.SetScalar(themeNew, "nav.display_title", BoolText(ChkNavDisplayTitle));
                themeNew = YamlPathEditor.SetScalar(themeNew, "nav.fixed", BoolText(ChkNavFixed));
                themeNew = YamlPathEditor.SetScalar(themeNew, "footer.custom_text", TxtFooterText.Text.Trim());
                themeNew = YamlPathEditor.SetScalar(themeNew, "footer.copyright.enable", BoolText(ChkCopyright));
                themeNew = YamlPathEditor.SetScalar(themeNew, "footer.owner.enable", BoolText(ChkOwner));

                // ---------- 评论系统 ----------
                string useValue = (CmbComments.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
                themeNew = YamlPathEditor.SetScalar(themeNew, "comments.use", useValue);
                themeNew = YamlPathEditor.SetScalar(themeNew, "giscus.repo", TxtGiscusRepo.Text.Trim());
                themeNew = YamlPathEditor.SetScalar(themeNew, "giscus.repo_id", TxtGiscusRepoId.Text.Trim());
                themeNew = YamlPathEditor.SetScalar(themeNew, "giscus.category_id", TxtGiscusCategoryId.Text.Trim());

                // 菜单只有真的改过才重写整块，避免无谓地抹掉块内注释
                if (!string.Equals(TxtMenu.Text, _originalMenuText, StringComparison.Ordinal))
                    themeNew = YamlPathEditor.SetMapping(themeNew, "menu", ParseMenu(TxtMenu.Text));

                if (!string.Equals(themeNew, _themeContent, StringComparison.Ordinal))
                {
                    backups.Add(_service.Backup(_themePath));
                    _service.Save(_themePath, themeNew);
                    _service.CleanOldBackups(_themePath);
                    _themeContent = themeNew;
                    _originalMenuText = TxtMenu.Text;
                }
            }

            if (backups.Count == 0)
            {
                SetStatus("没有检测到实际修改，未写入任何文件。");
                return;
            }

            string names = string.Join("、", backups.Select(Path.GetFileName));
            SetStatus($"已保存。原文件已备份为：{names}（最多保留 10 份备份）");
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

    private async void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        bool dirty = TxtStatus.Text.Contains("修改", StringComparison.Ordinal);

        if (dirty)
        {
            var owner = Window.GetWindow(this);
            string message = "重新加载会丢弃当前未保存的修改，确定继续吗？";

            var result = owner is null
                ? MessageBox.Show(message, "重新加载", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No)
                : MessageBox.Show(owner, message, "重新加载", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
                return;
        }

        await LoadAsync();
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

    private void MarkDirty()
    {
        SetStatus("有未保存的修改 · 点右下角「保存修改」写入文件");
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
