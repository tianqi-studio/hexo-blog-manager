namespace HexoBlogManager.Models;

/// <summary>
/// 本地持久化的应用设置。
/// 存放位置：%AppData%\HexoBlogManager\settings.json
/// 之所以不放在程序目录，是为了避免把程序装到 Program Files 后没有写权限的问题。
/// </summary>
public sealed class AppSettings
{
    /// <summary>上次选中的 Hexo 博客根目录（绝对路径）。null 表示尚未选择。</summary>
    public string? BlogPath { get; set; }

    /// <summary>最近使用过的博客目录，最新的排在最前面，用于快速切换。</summary>
    public List<string> RecentPaths { get; set; } = new();

    /// <summary>最后一次成功打开博客的时间，仅用于界面展示。</summary>
    public DateTimeOffset? LastOpenedAt { get; set; }

    /// <summary>一键发布到 GitHub Pages 的配置。首次启动时为 null，由界面填入默认值。</summary>
    public GitHubPublishSettings? GitHub { get; set; }
}
