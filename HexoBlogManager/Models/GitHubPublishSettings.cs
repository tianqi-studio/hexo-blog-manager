namespace HexoBlogManager.Models;

/// <summary>
/// 一键发布到 GitHub Pages 所需的配置。
/// 与 AppSettings 一起存到 %AppData%\HexoBlogManager\settings.json。
///
/// 注意：Token 不存明文，只存 DPAPI 加密后的 Base64 串（见 SecretProtector）。
/// DPAPI 用「当前登录用户」作用域，换用户或换机器都解不开 —— 这是有意为之：
/// settings.json 就算被别人拷走，也拿不到你的 GitHub 权限。
/// </summary>
public sealed class GitHubPublishSettings
{
    /// <summary>仓库所有者（用户名或组织名）。</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>仓库名。若为「用户名.github.io」，发布后站点地址是根域名。</summary>
    public string Repo { get; set; } = string.Empty;

    /// <summary>GitHub Pages 发布分支，默认 gh-pages（与 hexo-deployer-git 的惯例一致）。</summary>
    public string Branch { get; set; } = "gh-pages";

    /// <summary>提交信息模板，支持 {date} 占位符。</summary>
    public string CommitMessage { get; set; } = "Site updated: {date}";

    /// <summary>DPAPI 加密后的 Personal Access Token（Base64）。</summary>
    public string EncryptedToken { get; set; } = string.Empty;

    /// <summary>发布前是否先执行 hexo clean（清掉 public 与缓存，避免残留旧文件）。</summary>
    public bool CleanBeforeBuild { get; set; } = true;

    /// <summary>是否往 public 里写 .nojekyll（让 GitHub Pages 跳过 Jekyll 处理）。</summary>
    public bool WriteNoJekyll { get; set; } = true;

    /// <summary>推送成功后是否自动调用 API 开启 GitHub Pages。</summary>
    public bool AutoEnablePages { get; set; } = true;

    /// <summary>自定义远程地址（可选）。留空则按 https://github.com/Owner/Repo.git 生成。</summary>
    public string RemoteUrl { get; set; } = string.Empty;

    /// <summary>HTTP / HTTPS 代理（可选），例如 http://127.0.0.1:7890。国内访问 GitHub 常用。</summary>
    public string Proxy { get; set; } = string.Empty;

    /// <summary>最后一次发布成功的时间（仅用于界面展示）。</summary>
    public DateTimeOffset? LastPublishedAt { get; set; }

    /// <summary>最后一次发布成功后的站点地址（仅用于界面展示与快速打开）。</summary>
    public string LastPublishedUrl { get; set; } = string.Empty;

    /// <summary>是否已经填够了发布所需的最小信息。</summary>
    /// <remarks>计算属性，序列化时跳过，避免在 settings.json 里写出一堆无意义的只读字段。</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(EncryptedToken) &&
        (!string.IsNullOrWhiteSpace(RemoteUrl) ||
         (!string.IsNullOrWhiteSpace(Owner) && !string.IsNullOrWhiteSpace(Repo)));
}

/// <summary>GitHub 仓库的当前状态快照，用于发布前的「检测仓库」。</summary>
public sealed class GitHubRepoStatus
{
    /// <summary>仓库是否存在（且当前 Token 可见）。</summary>
    public bool Exists { get; init; }

    /// <summary>owner/repo 形式的全名。</summary>
    public string FullName { get; init; } = string.Empty;

    /// <summary>是否私有仓库。私有仓库无法用免费账号开 Pages（Pro 除外）。</summary>
    public bool IsPrivate { get; init; }

    /// <summary>仓库默认分支。</summary>
    public string DefaultBranch { get; init; } = string.Empty;

    /// <summary>仓库网页地址。</summary>
    public string HtmlUrl { get; init; } = string.Empty;

    /// <summary>GitHub Pages 是否已开启。</summary>
    public bool PagesEnabled { get; init; }

    /// <summary>Pages 当前使用的分支（未开启时为空）。</summary>
    public string PagesBranch { get; init; } = string.Empty;

    /// <summary>Pages 的访问地址（未开启时为空）。</summary>
    public string PagesUrl { get; init; } = string.Empty;

    /// <summary>Pages 最近一次构建状态（built / building / errored）。</summary>
    public string PagesStatus { get; init; } = string.Empty;
}
