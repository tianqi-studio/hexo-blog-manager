using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HexoBlogManager.Models;

namespace HexoBlogManager.Services;

/// <summary>
/// GitHub REST API 调用失败时抛出的异常，携带 HTTP 状态码与中文说明。
/// </summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP 状态码；0 表示根本没连上（网络问题）。</summary>
    public int StatusCode { get; }
}

/// <summary>
/// GitHub REST API 的最小封装，只提供发布功能需要的几个接口：
/// · 校验 Token（GET /user）
/// · 查询仓库与 Pages 状态（GET /repos/{o}/{r}、GET .../pages）
/// · 创建仓库（POST /user/repos）
/// · 开启 / 切换 Pages 分支（POST / PUT .../pages）
///
/// 设计取舍：不引 Octokit.NET 这类 SDK，直接用 HttpClient + System.Text.Json。
/// 一是保持零第三方依赖，二是需要的接口就这几个，SDK 反而增加体积和版本风险。
/// </summary>
public sealed class GitHubApi : IDisposable
{
    /// <summary>
    /// API 基地址。正常情况是 https://api.github.com；
    /// 调试时可以指向本地 mock 服务（启动参数 --api-base），用于离线验证整条链路。
    /// </summary>
    public static string BaseUrl { get; set; } = "https://api.github.com";

    private readonly HttpClient _client;

    public GitHubApi(string token, string? proxy = null)
    {
        var handler = new HttpClientHandler();

        // 可选代理：国内访问 api.github.com 经常需要走本地代理（Clash / v2ray 等）
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            try
            {
                handler.Proxy = new WebProxy(proxy.Trim());
                handler.UseProxy = true;
            }
            catch
            {
                // 代理串写错就退回系统默认，不让它把整个功能卡死
            }
        }

        _client = new HttpClient(handler)
        {
            // 超时不能太长：网络不通时用户等 20 秒已经很难受了
            Timeout = TimeSpan.FromSeconds(20)
        };

        _client.DefaultRequestHeaders.UserAgent.ParseAdd("HexoBlogManager/1.0");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        if (!string.IsNullOrWhiteSpace(token))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    // ==================================================================
    // 对外接口
    // ==================================================================

    /// <summary>校验 Token 是否有效，返回登录名。Token 无效会抛 GitHubApiException。</summary>
    public async Task<string> GetLoginAsync(CancellationToken ct = default)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "/user", null, ct).ConfigureAwait(false);

        if (status == 200)
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("login", out var login) ? login.GetString() ?? string.Empty : string.Empty;
        }

        throw BuildException(status, body, "校验 Token");
    }

    /// <summary>查询仓库状态（含 Pages）。仓库不存在返回 Exists=false，不抛异常。</summary>
    public async Task<GitHubRepoStatus> GetRepoStatusAsync(string owner, string repo, CancellationToken ct = default)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"/repos/{owner}/{repo}", null, ct).ConfigureAwait(false);

        if (status == 404)
            return new GitHubRepoStatus { Exists = false, FullName = $"{owner}/{repo}" };

        if (status != 200)
            throw BuildException(status, body, "查询仓库");

        string fullName = $"{owner}/{repo}";
        bool isPrivate = false;
        string defaultBranch = string.Empty;
        string htmlUrl = string.Empty;

        using (var doc = JsonDocument.Parse(body))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("full_name", out var fn)) fullName = fn.GetString() ?? fullName;
            if (root.TryGetProperty("private", out var pv)) isPrivate = pv.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("default_branch", out var db)) defaultBranch = db.GetString() ?? string.Empty;
            if (root.TryGetProperty("html_url", out var hu)) htmlUrl = hu.GetString() ?? string.Empty;
        }

        // Pages 状态单独查一次；失败不影响仓库本身的结论
        bool pagesEnabled = false;
        string pagesBranch = string.Empty;
        string pagesUrl = string.Empty;
        string pagesStatus = string.Empty;

        try
        {
            var (pStatus, pBody) = await SendAsync(HttpMethod.Get, $"/repos/{owner}/{repo}/pages", null, ct)
                .ConfigureAwait(false);

            if (pStatus == 200)
            {
                pagesEnabled = true;
                using var doc = JsonDocument.Parse(pBody);
                var root = doc.RootElement;
                if (root.TryGetProperty("html_url", out var h)) pagesUrl = h.GetString() ?? string.Empty;
                if (root.TryGetProperty("status", out var st)) pagesStatus = st.GetString() ?? string.Empty;
                if (root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object &&
                    src.TryGetProperty("branch", out var br))
                    pagesBranch = br.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // 细粒度 Token 可能没有 Pages 读权限，这里静默忽略
        }

        return new GitHubRepoStatus
        {
            Exists = true,
            FullName = fullName,
            IsPrivate = isPrivate,
            DefaultBranch = defaultBranch,
            HtmlUrl = htmlUrl,
            PagesEnabled = pagesEnabled,
            PagesBranch = pagesBranch,
            PagesUrl = pagesUrl,
            PagesStatus = pagesStatus
        };
    }

    /// <summary>创建仓库（默认 public）。已存在时抛异常，由调用方识别「已存在」后忽略。</summary>
    public async Task CreateRepoAsync(string repoName, bool isPrivate = false, CancellationToken ct = default)
    {
        var payload = new
        {
            name = repoName,
            @private = isPrivate,
            auto_init = false,
            description = "Hexo blog generated by Hexo 博客本地管理"
        };

        var (status, body) = await SendAsync(HttpMethod.Post, "/user/repos", payload, ct).ConfigureAwait(false);

        if (status == 201)
            return;

        throw BuildException(status, body, "创建仓库");
    }

    /// <summary>
    /// 开启或切换 GitHub Pages 的发布分支。
    /// 返回是否成功、给用户看的说明、以及站点地址（能拿到就拿）。
    /// </summary>
    public async Task<(bool Ok, string Message, string Url)> EnablePagesAsync(
        string owner, string repo, string branch, CancellationToken ct = default)
    {
        // 先看当前状态：已开启且分支一致就不动；分支不同则切换
        string currentBranch = string.Empty;
        bool enabled = false;

        try
        {
            var (s, b) = await SendAsync(HttpMethod.Get, $"/repos/{owner}/{repo}/pages", null, ct).ConfigureAwait(false);
            if (s == 200)
            {
                enabled = true;
                using var doc = JsonDocument.Parse(b);
                if (doc.RootElement.TryGetProperty("source", out var src) &&
                    src.ValueKind == JsonValueKind.Object &&
                    src.TryGetProperty("branch", out var br))
                    currentBranch = br.GetString() ?? string.Empty;
            }
        }
        catch (GitHubApiException)
        {
            // 读不到就当没开启，继续往下尝试创建
        }

        if (enabled && string.Equals(currentBranch, branch, StringComparison.OrdinalIgnoreCase))
            return (true, $"GitHub Pages 已在使用 {branch} 分支，无需改动。", BuildPagesUrl(owner, repo));

        var payload = new
        {
            build_type = "legacy",
            source = new { branch, path = "/" }
        };

        var (status, body) = enabled
            ? await SendAsync(HttpMethod.Put, $"/repos/{owner}/{repo}/pages", payload, ct).ConfigureAwait(false)
            : await SendAsync(HttpMethod.Post, $"/repos/{owner}/{repo}/pages", payload, ct).ConfigureAwait(false);

        if (status is 201 or 202 or 204)
        {
            string url = BuildPagesUrl(owner, repo);
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("html_url", out var h) &&
                        !string.IsNullOrWhiteSpace(h.GetString()))
                        url = h.GetString()!;
                }
                catch { /* 响应体不是 JSON 就当没有 */ }
            }

            return (true, enabled ? $"GitHub Pages 已切换到 {branch} 分支。" : $"GitHub Pages 已开启（源分支 {branch}）。", url);
        }

        // 特殊情况：GitHub 有时返回 409 表示「已存在」，此时按已开启处理
        if (status == 409)
            return (true, "GitHub Pages 已经是开启状态。", BuildPagesUrl(owner, repo));

        var ex = BuildException(status, body, "开启 GitHub Pages");
        return (false, ex.Message, BuildPagesUrl(owner, repo));
    }

    /// <summary>
    /// 推算 GitHub Pages 站点地址：
    /// · 仓库名是 owner.github.io → https://owner.github.io/
    /// · 其他仓库 → https://owner.github.io/repo/
    /// </summary>
    public static string BuildPagesUrl(string owner, string repo)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            return string.Empty;

        string userSite = owner + ".github.io";
        return repo.Equals(userSite, StringComparison.OrdinalIgnoreCase)
            ? $"https://{owner.ToLowerInvariant()}.github.io/"
            : $"https://{owner.ToLowerInvariant()}.github.io/{repo}/";
    }

    // ==================================================================
    // 内部：HTTP 与错误翻译
    // ==================================================================

    private async Task<(int Status, string Body)> SendAsync(
        HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BaseUrl.TrimEnd('/') + path);

        if (payload is not null)
        {
            string json = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ((int)response.StatusCode, body);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitHubApiException(0,
                "连接 GitHub 超时（20 秒无响应）。\n" +
                "国内网络访问 api.github.com 经常不稳定，可以在「高级选项」里填本地代理后重试。");
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubApiException(0,
                "无法连接 GitHub API。\n" +
                $"底层错误：{ex.Message}\n" +
                "可能原因：网络不通 / 被防火墙拦截 / 需要代理。");
        }
    }

    /// <summary>把 GitHub 的错误响应翻译成能指导操作的中文。</summary>
    private static GitHubApiException BuildException(int status, string body, string action)
    {
        string detail = ExtractMessage(body);

        return status switch
        {
            401 => new GitHubApiException(status,
                $"{action}失败：Token 无效或已过期（401）。\n" +
                "请到 GitHub → Settings → Developer settings → Personal access tokens 重新生成，" +
                "勾选 repo（或细粒度 Token 的 Contents / Pages 读写权限）。"),

            403 => new GitHubApiException(status,
                $"{action}失败：权限不足或被限流（403）。\n" +
                $"服务端说明：{detail}\n" +
                "常见原因：Token 缺少 repo / Pages 权限；或免费额度请求过快。"),

            404 => new GitHubApiException(status,
                $"{action}失败：仓库不存在或当前 Token 无权访问（404）。\n" +
                "请检查 owner/repo 拼写，以及 Token 是否有该仓库的访问权限。"),

            409 => new GitHubApiException(status,
                $"{action}失败：状态冲突（409）。\n服务端说明：{detail}"),

            422 => new GitHubApiException(status,
                $"{action}失败：提交的数据不被接受（422）。\n服务端说明：{detail}\n" +
                "如果是在开启 Pages：请先确认目标分支已经推送到仓库。"),

            _ => new GitHubApiException(status,
                $"{action}失败（HTTP {status}）。\n服务端说明：{detail}")
        };
    }

    /// <summary>从错误 JSON 里取 message 字段，取不到就截断原始响应。</summary>
    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "（无响应内容）";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg))
            {
                string text = msg.GetString() ?? string.Empty;
                if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                    errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                {
                    text += "\n" + errors[0].ToString();
                }
                return text;
            }
        }
        catch { /* 不是 JSON，走下面的截断逻辑 */ }

        return body.Length > 300 ? body[..300] + "…" : body;
    }

    public void Dispose() => _client.Dispose();
}
