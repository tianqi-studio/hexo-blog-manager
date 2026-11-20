namespace HexoBlogManager.Models;

/// <summary>
/// 一个 Hexo 博客站点的基本信息快照，由 BlogSiteService 校验目录时解析得出。
/// </summary>
public sealed class BlogSiteInfo
{
    /// <summary>博客根目录的绝对路径（已规范化）。</summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>_config.yml 里的网站标题 title。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>_config.yml 里的 subtitle。</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>_config.yml 里的 author。</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>_config.yml 里启用的主题名（theme 字段）。</summary>
    public string Theme { get; init; } = string.Empty;

    /// <summary>主题是否安装在 node_modules 下（用于提示「主题未安装」）。</summary>
    public bool ThemeInstalled { get; init; }

    /// <summary>source/_posts 下的 Markdown 文章数量（递归统计）。</summary>
    public int PostCount { get; init; }

    /// <summary>_config.yml 的完整路径。</summary>
    public string ConfigPath { get; init; } = string.Empty;

    /// <summary>_config.yml 的最后修改时间。</summary>
    public DateTime? ConfigLastWrite { get; init; }

    /// <summary>source/_posts 的完整路径。</summary>
    public string PostsPath { get; init; } = string.Empty;
}

/// <summary>
/// 目录校验结果。区分「是否合法」与「为什么不合法的排查建议」，便于界面给出有针对性的提示。
/// </summary>
public sealed class ValidationResult
{
    private ValidationResult() { }

    /// <summary>目录是否通过校验。</summary>
    public bool IsValid { get; private init; }

    /// <summary>一句话结论，直接显示在界面上。</summary>
    public string Message { get; private init; } = string.Empty;

    /// <summary>排查建议，多行文本，校验失败时告诉用户「可能是什么情况、该怎么处理」。</summary>
    public string Hint { get; private init; } = string.Empty;

    /// <summary>校验通过时的站点信息。</summary>
    public BlogSiteInfo? Site { get; private init; }

    public static ValidationResult Ok(BlogSiteInfo site) => new()
    {
        IsValid = true,
        Message = "目录校验通过",
        Site = site
    };

    public static ValidationResult Fail(string message, string hint = "") => new()
    {
        IsValid = false,
        Message = message,
        Hint = hint
    };
}
