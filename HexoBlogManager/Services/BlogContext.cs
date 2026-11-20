using HexoBlogManager.Models;

namespace HexoBlogManager.Services;

/// <summary>
/// 全局「当前博客」状态容器（进程内单例）。
///
/// 为什么需要它？
/// 模块二（文章）、模块三（命令）、模块四（配置）都需要知道用户当前选的是哪个博客。
/// 如果每个页面各自去读 settings.json，一是重复 IO，二是容易状态不一致。
/// 统一放在这里，任何模块通过 BlogContext.Instance.CurrentBlogPath 拿到当前路径。
///
/// 单例实现用 static 属性 + 私有构造，不引入 DI 容器，保持轻量。
/// </summary>
public sealed class BlogContext
{
    public static BlogContext Instance { get; } = new();

    private readonly SettingsService _settingsService = new();

    private BlogContext()
    {
        // 启动时先把上次保存的路径读出来，界面上可以直接展示（哪怕还没校验）
        try
        {
            var settings = _settingsService.Load();
            SavedBlogPath = settings.BlogPath;
            RecentPaths = settings.RecentPaths ?? new List<string>();
        }
        catch
        {
            SavedBlogPath = null;
            RecentPaths = new List<string>();
        }
    }

    /// <summary>上次保存的博客路径（可能已失效，尚未校验）。</summary>
    public string? SavedBlogPath { get; private set; }

    /// <summary>最近使用过的博客路径。</summary>
    public IReadOnlyList<string> RecentPaths { get; private set; }

    /// <summary>当前校验通过的站点信息；未连接时为 null。</summary>
    public BlogSiteInfo? CurrentSite { get; private set; }

    /// <summary>当前博客根目录。优先返回校验通过的路径，否则返回上次保存的路径。</summary>
    public string? CurrentBlogPath => CurrentSite?.RootPath ?? SavedBlogPath;

    /// <summary>当前博客是否已通过校验。</summary>
    public bool IsConnected => CurrentSite is not null;

    /// <summary>站点连接成功（或切换到新站点）时触发，主窗口据此刷新底部状态。</summary>
    public event EventHandler<BlogSiteInfo>? SiteConnected;

    /// <summary>
    /// 记录一个校验通过的站点：更新内存状态 + 落盘保存 + 广播事件。
    /// </summary>
    public void SetCurrentSite(BlogSiteInfo site)
    {
        ArgumentNullException.ThrowIfNull(site);

        CurrentSite = site;
        SavedBlogPath = site.RootPath;

        // 落盘（失败不影响使用，只是下次启动不会自动加载）
        _settingsService.RememberBlogPath(site.RootPath);

        try
        {
            RecentPaths = _settingsService.Load().RecentPaths;
        }
        catch
        {
            // 读失败就沿用旧列表
        }

        SiteConnected?.Invoke(this, site);
    }

    /// <summary>站点失效时清空内存状态（不清除磁盘上的路径记录，方便用户重新连接）。</summary>
    public void ClearCurrentSite()
    {
        CurrentSite = null;
    }

    /// <summary>
    /// 确保当前站点已就绪。
    ///
    /// 为什么需要它？
    /// 用户如果是直接打开「文章管理」页（而不是先经过站点页），CurrentSite 还没被校验过，
    /// 只有磁盘上记录的 SavedBlogPath。这里按需补一次校验，保证各模块都能拿到可用站点。
    /// </summary>
    public async Task<BlogSiteInfo?> EnsureSiteAsync()
    {
        if (CurrentSite is not null)
            return CurrentSite;

        if (string.IsNullOrWhiteSpace(SavedBlogPath))
            return null;

        var service = new BlogSiteService();
        var result = await Task.Run(() => service.Validate(SavedBlogPath!));

        if (!result.IsValid || result.Site is null)
            return null;

        SetCurrentSite(result.Site);
        return result.Site;
    }
}
