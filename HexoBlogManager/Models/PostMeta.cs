using System.Globalization;

namespace HexoBlogManager.Models;

/// <summary>
/// 一篇文章的元信息 + 正文。
/// 字段严格对齐 Butterfly 主题常用的 front-matter 约定：
/// title / date / updated / tags / categories / top / comments
/// </summary>
public sealed class PostMeta
{
    /// <summary>Markdown 文件的绝对路径。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>相对 source/_posts 的路径，例如 "2026/hello-world.md"。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>文件名（含扩展名），如 "hello-world.md"。</summary>
    public string FileName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>date 字段的原始文本（保存时尽量保持用户写法）。</summary>
    public string DateText { get; set; } = string.Empty;

    public DateTime? Date { get; set; }
    public DateTime? Updated { get; set; }

    public List<string> Tags { get; set; } = new();
    public List<string> Categories { get; set; } = new();

    /// <summary>是否置顶。Butterfly 里 top: 1 或 top: true 都算置顶。</summary>
    public bool Top { get; set; }

    /// <summary>是否开启评论，默认开启。</summary>
    public bool Comments { get; set; } = true;

    public DateTime FileModified { get; set; }
    public long FileSize { get; set; }

    /// <summary>front-matter 之后的正文（不含 ---）。</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>原始 front-matter 结构，保存时据此保留用户自定义字段。</summary>
    public Services.FrontMatterBlock FrontMatter { get; set; } = new();

    // ---------- 以下是给列表界面直接绑定的展示属性 ----------

    public string DateDisplay => Date?.ToString("yyyy-MM-dd") ?? "—";
    public string DateFullDisplay => Date?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
    public string UpdatedDisplay => Updated?.ToString("yyyy-MM-dd") ?? "—";
    public string UpdatedFullDisplay => Updated?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
    public string TagsDisplay => Tags.Count == 0 ? "—" : string.Join(" · ", Tags);
    public string CategoriesDisplay => Categories.Count == 0 ? "—" : string.Join(" / ", Categories);
    public string TopDisplay => Top ? "置顶" : string.Empty;
    public string CommentsDisplay => Comments ? "开" : "关";
    public string SizeDisplay => FileSize < 1024
        ? $"{FileSize} B"
        : FileSize < 1024 * 1024
            ? $"{FileSize / 1024.0:F1} KB"
            : $"{FileSize / 1024.0 / 1024.0:F1} MB";

    /// <summary>
    /// 文件名列的悬停提示：文件名 + 大小 + 最后修改时间。
    /// 这三项在列表里挤不下（需求定的是 8 列），放在 ToolTip 里最合适。
    /// </summary>
    public string FileInfoDisplay =>
        $"{FileName}\n{SizeDisplay} · 最后修改 {FileModified:yyyy-MM-dd HH:mm}";

    /// <summary>搜索匹配：标题、标签、分类、文件名任一命中即算命中（忽略大小写）。</summary>
    public bool Matches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return true;

        keyword = keyword.Trim();

        return Contains(Title, keyword)
            || Contains(FileName, keyword)
            || Tags.Any(t => Contains(t, keyword))
            || Categories.Any(c => Contains(c, keyword));
    }

    private static bool Contains(string source, string keyword)
        => !string.IsNullOrEmpty(source)
           && source.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析 Hexo 常见的日期写法。
    /// 支持：2026-11-20 20:00:00 / 2026-11-20 20:00 / 2026-11-20 /
    ///      2026/11/20 / 2026-11-20T20:00:00 等。
    /// </summary>
    public static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim().Trim('"', '\'');

        string[] formats =
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd HH:mm",
            "yyyy/MM/dd",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ssZ",
            "yyyy-MM-ddTHH:mm:ss.fffZ"
        };

        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact))
            return exact;

        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var loose))
            return loose;

        return null;
    }

    public static string FormatDate(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss");
}
