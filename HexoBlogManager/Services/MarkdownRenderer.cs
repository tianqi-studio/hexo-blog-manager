using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HexoBlogManager.Services;

/// <summary>
/// 轻量 Markdown → FlowDocument 渲染器，仅用于编辑器右侧的实时预览。
///
/// 需求明确说「不要做复杂预览」，所以这里只做博客文章里最常见的语法：
/// 标题、段落、无序/有序列表、引用、代码块、分割线、
/// 行内代码、粗体、斜体、删除线、链接、图片。
///
/// 刻意不做：表格、脚注、数学公式、HTML 直出。
/// 好处是不引第三方库（Markdig 之类），且渲染结果与主题无关，只求「看得懂结构」。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6F, 0x76));
    private static readonly Brush BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE5, 0xE9));
    private static readonly Brush CodeBg = new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD));
    private static readonly FontFamily BodyFont = new("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, Microsoft YaHei UI");

    /// <summary>
    /// 渲染入口。
    /// </summary>
    /// <param name="markdown">Markdown 文本。</param>
    /// <param name="baseDirectory">当前文章所在目录，用于解析相对图片路径。</param>
    /// <param name="blogRoot">博客根目录，用于解析 /images/xxx 这类站内绝对路径。</param>
    public static FlowDocument Render(string? markdown, string? baseDirectory, string? blogRoot)
    {
        var doc = new FlowDocument
        {
            FontFamily = BodyFont,
            FontSize = 14,
            Foreground = TextBrush,
            PagePadding = new Thickness(14, 12, 14, 20),
            // 单栏铺满可用宽度：ColumnWidth 设为无限大表示只有一列，
            // 实际宽度由 FlowDocumentScrollViewer 决定，这样不会出现横向滚动条。
            ColumnWidth = double.PositiveInfinity,
            LineHeight = 24
        };

        string text = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = text.Split('\n');

        int i = 0;
        while (i < lines.Length)
        {
            string raw = lines[i];
            string trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            // ---------- 代码块 ----------
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                string lang = trimmed[3..].Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Append(lines[i]).Append('\n');
                    i++;
                }
                if (i < lines.Length) i++;   // 跳过结束标记

                doc.Blocks.Add(BuildCodeBlock(lang, code.ToString()));
                continue;
            }

            // ---------- 分割线 ----------
            if (IsHorizontalRule(trimmed))
            {
                doc.Blocks.Add(new BlockUIContainer(new Border
                {
                    Height = 1,
                    Background = BorderBrush,
                    Margin = new Thickness(0, 8, 0, 12)
                }));
                i++;
                continue;
            }

            // ---------- 标题 ----------
            if (trimmed[0] == '#')
            {
                int level = 0;
                while (level < trimmed.Length && trimmed[level] == '#' && level < 6)
                    level++;

                if (level > 0 && level < trimmed.Length && trimmed[level] == ' ')
                {
                    string headingText = trimmed[(level + 1)..].Trim();
                    doc.Blocks.Add(BuildHeading(headingText, level, baseDirectory, blogRoot));
                    i++;
                    continue;
                }
            }

            // ---------- 引用 ----------
            if (trimmed.StartsWith('>'))
            {
                var quote = new StringBuilder();
                while (i < lines.Length && lines[i].Trim().StartsWith('>'))
                {
                    quote.Append(lines[i].Trim().TrimStart('>').Trim()).Append('\n');
                    i++;
                }

                var qp = new Paragraph
                {
                    BorderBrush = BorderBrush,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 2, 0, 2),
                    Margin = new Thickness(0, 6, 0, 12),
                    Foreground = MutedBrush
                };
                AppendInline(qp, quote.ToString().Trim(), baseDirectory, blogRoot);
                doc.Blocks.Add(qp);
                continue;
            }

            // ---------- 列表 ----------
            if (IsUnorderedItem(trimmed) || IsOrderedItem(trimmed))
            {
                doc.Blocks.Add(BuildList(lines, ref i, baseDirectory, blogRoot));
                continue;
            }

            // ---------- 普通段落（合并连续行） ----------
            var paragraphText = new StringBuilder();
            while (i < lines.Length)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || IsBlockStart(t))
                    break;

                if (paragraphText.Length > 0)
                    paragraphText.Append(' ');
                paragraphText.Append(t);
                i++;
            }

            if (paragraphText.Length == 0)
            {
                // 保险：极端情况下避免死循环
                i++;
                continue;
            }

            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
            AppendInline(p, paragraphText.ToString(), baseDirectory, blogRoot);
            doc.Blocks.Add(p);
        }

        if (doc.Blocks.Count == 0)
        {
            doc.Blocks.Add(new Paragraph(new Run("（正文为空，右侧会实时显示你输入的内容）"))
            {
                Foreground = MutedBrush,
                FontStyle = FontStyles.Italic
            });
        }

        return doc;
    }

    // ==================================================================
    // 块级元素
    // ==================================================================

    private static Block BuildHeading(string text, int level, string? baseDir, string? blogRoot)
    {
        double size = level switch
        {
            1 => 26,
            2 => 21,
            3 => 18,
            4 => 16,
            5 => 14.5,
            _ => 14
        };

        var p = new Paragraph
        {
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, level <= 2 ? 18 : 12, 0, 8),
            LineHeight = size * 1.5
        };

        AppendInline(p, text, baseDir, blogRoot);
        return p;
    }

    private static Block BuildCodeBlock(string language, string code)
    {
        var text = new TextBlock
        {
            Text = code.TrimEnd('\n'),
            FontFamily = MonoFont,
            FontSize = 12.5,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        };

        var stack = new StackPanel();

        if (!string.IsNullOrWhiteSpace(language))
        {
            stack.Children.Add(new TextBlock
            {
                Text = language,
                FontSize = 10.5,
                Foreground = MutedBrush,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        stack.Children.Add(text);

        var border = new Border
        {
            Background = CodeBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 4, 0, 14),
            Child = stack
        };

        return new BlockUIContainer(border);
    }

    private static List BuildList(string[] lines, ref int index, string? baseDir, string? blogRoot)
    {
        bool ordered = IsOrderedItem(lines[index].Trim());

        var list = new List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 2, 0, 12),
            Padding = new Thickness(18, 0, 0, 0)
        };

        while (index < lines.Length)
        {
            string trimmed = lines[index].Trim();

            bool isItem = ordered ? IsOrderedItem(trimmed) : IsUnorderedItem(trimmed);
            if (!isItem)
                break;

            string content = ordered
                ? trimmed[(trimmed.IndexOf('.') + 1)..].Trim()
                : trimmed[1..].Trim();

            var p = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
            AppendInline(p, content, baseDir, blogRoot);

            list.ListItems.Add(new ListItem(p));
            index++;
        }

        return list;
    }

    // ==================================================================
    // 行内解析
    // ==================================================================

    /// <summary>
    /// 逐字符扫描行内语法。顺序很重要：先处理 ![]() 再处理 []()，
    /// 否则图片会被当成普通链接。
    /// </summary>
    private static void AppendInline(Paragraph paragraph, string text, string? baseDir, string? blogRoot)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var buffer = new StringBuilder();
        int i = 0;

        void Flush()
        {
            if (buffer.Length == 0) return;
            paragraph.Inlines.Add(new Run(buffer.ToString()));
            buffer.Clear();
        }

        while (i < text.Length)
        {
            char c = text[i];

            // 图片 ![alt](src)
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[' &&
                TryParseLink(text, i + 1, out string imgAlt, out string imgSrc, out int imgLen))
            {
                Flush();
                AddInlineImage(paragraph, imgAlt, imgSrc, baseDir, blogRoot);
                i += imgLen + 1;   // +1 跳过前面的 !
                continue;
            }

            // 链接 [text](url)
            if (c == '[' && TryParseLink(text, i, out string linkText, out string linkUrl, out int linkLen))
            {
                Flush();
                var hyperlink = new Hyperlink(new Run(linkText))
                {
                    NavigateUri = Uri.TryCreate(linkUrl, UriKind.Absolute, out var uri) ? uri : null,
                    Foreground = AccentBrush,
                    TextDecorations = null
                };
                hyperlink.ToolTip = linkUrl;
                paragraph.Inlines.Add(hyperlink);
                i += linkLen;
                continue;
            }

            // 行内代码 `code`
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    paragraph.Inlines.Add(new Run(text[(i + 1)..end])
                    {
                        FontFamily = MonoFont,
                        Background = CodeBg,
                        FontSize = 13
                    });
                    i = end + 1;
                    continue;
                }
            }

            // 粗体 **text**
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    paragraph.Inlines.Add(new Run(text[(i + 2)..end]) { FontWeight = FontWeights.Bold });
                    i = end + 2;
                    continue;
                }
            }

            // 斜体 *text*
            if (c == '*')
            {
                int end = text.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    paragraph.Inlines.Add(new Run(text[(i + 1)..end]) { FontStyle = FontStyles.Italic });
                    i = end + 1;
                    continue;
                }
            }

            // 删除线 ~~text~~
            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                int end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    paragraph.Inlines.Add(new Run(text[(i + 2)..end])
                    {
                        TextDecorations = TextDecorations.Strikethrough
                    });
                    i = end + 2;
                    continue;
                }
            }

            buffer.Append(c);
            i++;
        }

        Flush();
    }

    /// <summary>
    /// 解析 [文字](地址) 结构。index 指向 "["。
    /// 返回的总长度包含方括号和圆括号。
    /// </summary>
    private static bool TryParseLink(string text, int index, out string label, out string url, out int length)
    {
        label = string.Empty;
        url = string.Empty;
        length = 0;

        if (index >= text.Length || text[index] != '[')
            return false;

        int closeBracket = text.IndexOf(']', index + 1);
        if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(')
            return false;

        int closeParen = text.IndexOf(')', closeBracket + 2);
        if (closeParen < 0)
            return false;

        label = text[(index + 1)..closeBracket];
        url = text[(closeBracket + 2)..closeParen].Trim();
        length = closeParen - index + 1;
        return true;
    }

    private static void AddInlineImage(Paragraph paragraph, string alt, string src,
        string? baseDir, string? blogRoot)
    {
        string? path = ResolveImagePath(src, baseDir, blogRoot);

        if (path is not null && File.Exists(path))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                // OnLoad：读完立即释放文件句柄，否则用户后续覆盖图片会失败
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                var image = new Image
                {
                    Source = bitmap,
                    MaxWidth = 460,
                    MaxHeight = 340,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(2, 4, 2, 4),
                    ToolTip = string.IsNullOrWhiteSpace(alt) ? src : alt
                };

                paragraph.Inlines.Add(new InlineUIContainer(image)
                {
                    BaselineAlignment = BaselineAlignment.Center
                });
                return;
            }
            catch
            {
                // 图片损坏：走下面的占位逻辑
            }
        }

        // 无法加载（外链、文件不存在、格式不支持）：显示占位文字，不打断排版
        paragraph.Inlines.Add(new Run($"[图片：{(string.IsNullOrWhiteSpace(alt) ? src : alt)}]")
        {
            Foreground = MutedBrush,
            FontStyle = FontStyles.Italic,
            Background = CodeBg
        });
    }

    /// <summary>
    /// 解析图片真实路径：
    /// · /images/a.png  → 博客根/source/images/a.png
    /// · ./a.png       → 文章所在目录/a.png
    /// · http(s)://    → 返回 null（不加载远程图片，避免卡界面）
    /// </summary>
    private static string? ResolveImagePath(string src, string? baseDir, string? blogRoot)
    {
        if (string.IsNullOrWhiteSpace(src))
            return null;

        src = src.Trim();

        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            if (src.StartsWith('/'))
            {
                if (string.IsNullOrWhiteSpace(blogRoot))
                    return null;

                string relative = src.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                return Path.Combine(blogRoot, "source", relative);
            }

            if (string.IsNullOrWhiteSpace(baseDir))
                return null;

            return Path.GetFullPath(Path.Combine(baseDir, src.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            return null;
        }
    }

    // ==================================================================
    // 语法判断
    // ==================================================================

    private static bool IsHorizontalRule(string trimmed)
    {
        if (trimmed.Length < 3)
            return false;

        char c = trimmed[0];
        if (c != '-' && c != '*' && c != '_')
            return false;

        foreach (char ch in trimmed)
        {
            if (ch != c && ch != ' ')
                return false;
        }

        return trimmed.Count(ch => ch == c) >= 3;
    }

    private static bool IsUnorderedItem(string trimmed)
    {
        if (trimmed.Length < 2)
            return false;

        return (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+')
               && trimmed[1] == ' ';
    }

    private static bool IsOrderedItem(string trimmed)
    {
        int i = 0;
        while (i < trimmed.Length && char.IsDigit(trimmed[i]))
            i++;

        return i > 0 && i + 1 < trimmed.Length && trimmed[i] == '.' && trimmed[i + 1] == ' ';
    }

    /// <summary>判断这一行是否会开启一个新的块级元素（用于段落合并时中断）。</summary>
    private static bool IsBlockStart(string trimmed)
    {
        if (trimmed.StartsWith("```", StringComparison.Ordinal)) return true;
        if (trimmed.StartsWith('>')) return true;
        if (IsHorizontalRule(trimmed)) return true;
        if (IsUnorderedItem(trimmed) || IsOrderedItem(trimmed)) return true;

        if (trimmed[0] == '#')
        {
            int level = 0;
            while (level < trimmed.Length && trimmed[level] == '#')
                level++;
            if (level < trimmed.Length && trimmed[level] == ' ')
                return true;
        }

        return false;
    }
}
