using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using HexoBlogManager.Models;
using HexoBlogManager.Services;

namespace HexoBlogManager.Views;

/// <summary>
/// 文章编辑器窗口。
///
/// 左侧写 Markdown，右侧实时预览（防抖 350ms），顶部编辑 front-matter 字段。
/// 拖入图片会自动复制到 source/images/yyyy/MM/ 并插入 Markdown 链接。
///
/// 两种用法：
/// new PostEditorWindow(blogRoot)            → 新建
/// new PostEditorWindow(blogRoot, existing)  → 编辑
/// </summary>
public partial class PostEditorWindow : Window
{
    private readonly string _blogRoot;
    private readonly PostMeta? _existingPost;
    private readonly PostService _postService;
    private readonly DispatcherTimer _previewTimer;

    private bool _loading;              // 初始化填充阶段，忽略脏标记
    private bool _dirty;                // 是否有未保存修改
    private bool _fileNameTouched;      // 用户是否手动改过文件名

    /// <summary>保存成功后的文件名，供列表页显示提示。</summary>
    public string SavedFileName { get; private set; } = string.Empty;

    /// <summary>调试用：启动后自动打开「历史版本」窗口（配合 --screenshot 验证界面）。</summary>
    public static bool DebugOpenHistory { get; set; }

    // ------------------------------------------------------------------

    public PostEditorWindow(string blogRoot) : this(blogRoot, null) { }

    public PostEditorWindow(string blogRoot, PostMeta? existingPost)
    {
        InitializeComponent();

        _blogRoot = blogRoot;
        _existingPost = existingPost;
        _postService = new PostService(blogRoot);

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            UpdatePreview();
        };

        Loaded += OnLoaded;
        Closing += OnClosing;

        // 文件名联动：新建时标题变化自动生成文件名，除非用户自己改过
        TitleBox.TextChanged += (_, _) =>
        {
            if (_loading) return;
            MarkDirty();

            if (_existingPost is null && !_fileNameTouched)
            {
                _loading = true;
                FileNameBox.Text = PostService.SanitizeFileName(TitleBox.Text);
                _loading = false;
            }
        };

        FileNameBox.TextChanged += (_, _) =>
        {
            if (_loading || _existingPost is not null) return;
            _fileNameTouched = true;
            MarkDirty();
        };

        DateBox.TextChanged += (_, _) => MarkDirty();
        TagsBox.TextChanged += (_, _) => MarkDirty();
        CategoriesBox.TextChanged += (_, _) => MarkDirty();
        TopCheck.Checked += (_, _) => MarkDirty();
        TopCheck.Unchecked += (_, _) => MarkDirty();
        CommentsCheck.Checked += (_, _) => MarkDirty();
        CommentsCheck.Unchecked += (_, _) => MarkDirty();
    }

    // ==================================================================
    // 初始化
    // ==================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;

        try
        {
            if (_existingPost is not null)
            {
                Title = "编辑文章 · " + _existingPost.FileName;
                TxtHeader.Text = "编辑文章 · " + _existingPost.RelativePath;

                TitleBox.Text = _existingPost.Title;
                DateBox.Text = (_existingPost.Date ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss");

                FileNameBox.Text = _existingPost.FileName;
                FileNameBox.IsReadOnly = true;
                FileNameBox.ToolTip = "编辑已有文章时不修改文件名，避免已有链接失效。如需改名，请重命名文件后重新打开。";

                TagsBox.Text = string.Join(", ", _existingPost.Tags);
                CategoriesBox.Text = string.Join(", ", _existingPost.Categories);

                TopCheck.IsChecked = _existingPost.Top;
                CommentsCheck.IsChecked = _existingPost.Comments;

                Editor.Text = _existingPost.Body.TrimStart('\n');

                // 只有已有文章才有历史可看，新建时隐藏这个按钮
                BtnHistory.Visibility = Visibility.Visible;
            }
            else
            {
                Title = "新建文章";
                TxtHeader.Text = "新建文章 · 将保存到 source/_posts";

                DateBox.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                CommentsCheck.IsChecked = true;
                TopCheck.IsChecked = false;

                Editor.Text = string.Empty;
            }

            TxtStatus.Text = $"{Editor.Text.Length} 字";
        }
        finally
        {
            _loading = false;
            _dirty = false;
        }

        UpdatePreview();

        TitleBox.Focus();
        TitleBox.SelectAll();

        // 调试用：等布局完成后再弹历史窗口，否则截出来的窗口尺寸可能不对
        if (DebugOpenHistory && _existingPost is not null)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => BtnHistory_Click(this, new RoutedEventArgs())),
                DispatcherPriority.Background);
        }
    }

    // ==================================================================
    // 历史版本
    // ==================================================================

    private void BtnHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_existingPost is null)
            return;

        var dialog = new PostHistoryWindow(_postService, _existingPost) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Restored)
        {
            ReloadFromDisk();

            ShowMessage("已恢复到选中的版本。\n\n如果恢复得不合适，再打开「历史版本」还能退回刚才那一份。",
                "已恢复", MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// 从磁盘重新读取当前文章并刷新界面。
    /// 恢复历史版本之后必须走这一步：文件已经被替换，界面里还是旧内容，
    /// 此时再点保存会把恢复的内容又覆盖回去。
    /// </summary>
    private void ReloadFromDisk()
    {
        if (_existingPost is null)
            return;

        PostMeta fresh;

        try
        {
            fresh = _postService.ReadPost(_existingPost.FilePath);
        }
        catch (Exception ex)
        {
            ShowMessage($"重新读取文件失败：\n{ex.Message}", "读取失败", MessageBoxImage.Error);
            return;
        }

        // 同步到当前对象，保证后续保存基于最新读到的 front-matter，
        // 而不是恢复之前的那一份
        _existingPost.Title = fresh.Title;
        _existingPost.DateText = fresh.DateText;
        _existingPost.Date = fresh.Date;
        _existingPost.Updated = fresh.Updated;
        _existingPost.Tags = fresh.Tags;
        _existingPost.Categories = fresh.Categories;
        _existingPost.Top = fresh.Top;
        _existingPost.Comments = fresh.Comments;
        _existingPost.Body = fresh.Body;
        _existingPost.FrontMatter = fresh.FrontMatter;

        _loading = true;

        try
        {
            TitleBox.Text = fresh.Title;
            DateBox.Text = (fresh.Date ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss");
            TagsBox.Text = string.Join(", ", fresh.Tags);
            CategoriesBox.Text = string.Join(", ", fresh.Categories);
            TopCheck.IsChecked = fresh.Top;
            CommentsCheck.IsChecked = fresh.Comments;

            Editor.Text = fresh.Body.TrimStart('\n');
            TxtStatus.Text = $"{Editor.Text.Length} 字";
        }
        finally
        {
            _loading = false;
            _dirty = false;   // 界面内容与磁盘一致，不再算「未保存」
        }

        UpdatePreview();
    }

    // ==================================================================
    // 保存
    // ==================================================================

    private void BtnSave_Click(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        string title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            ShowMessage("标题不能为空。", "提示", MessageBoxImage.Warning);
            TitleBox.Focus();
            return;
        }

        DateTime? date = PostMeta.ParseDate(DateBox.Text);
        if (date is null)
        {
            ShowMessage("日期格式无法识别。\n\n请使用：yyyy-MM-dd HH:mm:ss\n例如：2026-11-20 20:30:00",
                "日期格式错误", MessageBoxImage.Warning);
            DateBox.Focus();
            DateBox.SelectAll();
            return;
        }

        try
        {
            if (_existingPost is null)
                SaveAsNew(title, date.Value);
            else
                SaveExisting(title, date.Value);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowMessage($"没有写入权限，保存失败：\n{ex.Message}", "保存失败", MessageBoxImage.Error);
            return;
        }
        catch (IOException ex)
        {
            ShowMessage($"写入文件失败：\n{ex.Message}\n\n可能文件正被编辑器占用，请关闭后再试。",
                "保存失败", MessageBoxImage.Error);
            return;
        }
        catch (Exception ex)
        {
            ShowMessage($"保存时出错：\n{ex.Message}", "保存失败", MessageBoxImage.Error);
            return;
        }

        _dirty = false;
        DialogResult = true;
    }

    private void SaveAsNew(string title, DateTime date)
    {
        string rawName = FileNameBox.Text.Trim();
        if (rawName.Length == 0)
            rawName = PostService.SanitizeFileName(title);
        if (rawName.Length == 0)
            rawName = DateTime.Now.ToString("yyyyMMddHHmmss");

        string nameWithoutExt = Path.GetFileNameWithoutExtension(rawName);

        var meta = new PostMeta
        {
            Title = title,
            Date = date,
            DateText = PostMeta.FormatDate(date),
            Updated = date,                       // 新文章的 updated 与 date 一致
            Tags = SplitList(TagsBox.Text),
            Categories = SplitList(CategoriesBox.Text),
            Top = TopCheck.IsChecked == true,
            Comments = CommentsCheck.IsChecked == true
        };

        string path = _postService.CreateNew(nameWithoutExt, meta, Editor.Text);
        SavedFileName = Path.GetFileName(path);
    }

    private void SaveExisting(string title, DateTime date)
    {
        var meta = _existingPost!;

        meta.Title = title;
        meta.Date = date;
        meta.DateText = PostMeta.FormatDate(date);
        meta.Updated = DateTime.Now;              // 编辑已有文章，updated 记为当前时间
        meta.Tags = SplitList(TagsBox.Text);
        meta.Categories = SplitList(CategoriesBox.Text);
        meta.Top = TopCheck.IsChecked == true;
        meta.Comments = CommentsCheck.IsChecked == true;

        _postService.Save(meta, Editor.Text);
        SavedFileName = meta.FileName;
    }

    /// <summary>标签/分类输入：支持中英文逗号、分号、换行分隔。</summary>
    private static List<string> SplitList(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text
            .Split(new[] { ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ==================================================================
    // 预览
    // ==================================================================

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        TxtStatus.Text = $"{Editor.Text.Length} 字";
        MarkDirty();

        // 防抖：停止输入 350ms 后才重新渲染，避免每个按键都重建 FlowDocument
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void UpdatePreview()
    {
        try
        {
            string baseDir = _existingPost is not null
                ? Path.GetDirectoryName(_existingPost.FilePath) ?? _postService.PostsDirectory
                : _postService.PostsDirectory;

            Preview.Document = MarkdownRenderer.Render(Editor.Text, baseDir, _blogRoot);
        }
        catch (Exception ex)
        {
            var doc = new FlowDocument();
            doc.Blocks.Add(new Paragraph(new Run("预览渲染失败：" + ex.Message)));
            Preview.Document = doc;
        }
    }

    // ==================================================================
    // 图片拖拽
    // ==================================================================

    private void Editor_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Editor_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;

        var images = files
            .Where(f => File.Exists(f) && PostService.IsImageExtension(Path.GetExtension(f)))
            .ToList();

        if (images.Count == 0)
        {
            ShowMessage("只支持拖入图片文件：png / jpg / jpeg / gif / webp / bmp / svg / avif。",
                "文件类型不支持", MessageBoxImage.Information);
            return;
        }

        // 插入到鼠标释放的位置；取不到就用当前光标位置
        int index = Editor.GetCharacterIndexFromPoint(e.GetPosition(Editor), true);
        if (index < 0) index = Editor.CaretIndex;

        var sb = new StringBuilder();
        var failures = new List<string>();

        foreach (string image in images)
        {
            try
            {
                sb.Append(_postService.ImportImage(image)).Append('\n');
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(image)}：{ex.Message}");
            }
        }

        if (sb.Length > 0)
        {
            string text = Editor.Text;

            // 保证图片链接不会黏在上一行文字末尾
            string prefix = index > 0 && text[index - 1] != '\n' ? "\n" : string.Empty;
            string insert = prefix + sb;

            Editor.Text = text.Insert(index, insert);
            Editor.CaretIndex = index + insert.Length;
            Editor.Focus();

            MarkDirty();
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        if (failures.Count > 0)
        {
            ShowMessage("以下图片转存失败：\n\n" + string.Join("\n", failures),
                "部分图片未插入", MessageBoxImage.Warning);
        }
    }

    // ==================================================================
    // 关闭确认
    // ==================================================================

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_dirty)
            return;

        var result = MessageBox.Show(this,
            "当前修改还没有保存，确定要关闭吗？\n\n关闭后本次修改会丢失。",
            "有未保存的修改", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
    }

    private void ShowMessage(string text, string caption, MessageBoxImage icon)
    {
        MessageBox.Show(this, text, caption, MessageBoxButton.OK, icon);
    }
}
