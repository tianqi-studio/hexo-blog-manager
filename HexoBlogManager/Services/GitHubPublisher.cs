using System.IO;
using System.Text;

namespace HexoBlogManager.Services;

/// <summary>一次发布任务的输入参数。</summary>
public sealed class PublishOptions
{
    public string BlogRoot { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string Branch { get; set; } = "gh-pages";
    public string CommitMessage { get; set; } = "Site updated: {date}";
    public string Token { get; set; } = string.Empty;
    public string Proxy { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    public bool CleanBeforeBuild { get; set; } = true;
    public bool WriteNoJekyll { get; set; } = true;
    public bool AutoEnablePages { get; set; } = true;
}

/// <summary>一次发布任务的结果。</summary>
public sealed class PublishResult
{
    public bool Success { get; init; }

    /// <summary>给用户看的一句话结论。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>站点访问地址（成功时一般都有）。</summary>
    public string? SiteUrl { get; init; }

    /// <summary>本次提交的短 SHA。</summary>
    public string? CommitShort { get; init; }

    /// <summary>GitHub Pages 是否已确认开启（API 调用成功）。</summary>
    public bool PagesEnabled { get; init; }

    /// <summary>本次推送的文件数量（git 报告的变更数，取不到为 0）。</summary>
    public int FileCount { get; init; }
}

/// <summary>
/// 一键发布器：把 Hexo 生成的 public 目录推到 GitHub 仓库的指定分支，并开启 GitHub Pages。
///
/// 为什么不用 hexo-deployer-git 插件？
/// 1) 那需要改用户的 _config.yml 并额外装 npm 包，一旦配置写错整个博客生成都会报错；
/// 2) 插件是黑盒，出错时只有一行 git 报错，用户不知道怎么排查；
/// 3) 我们要的不只是推送，还要「顺手把 Pages 打开」「告诉用户访问地址」，
///    这些是插件不管的，干脆自己用 git 命令行做，每一步都能打日志。
///
/// 推送策略：把 public 的内容同步到博客根目录下的 .deploy_git（独立仓库），
/// 提交后 --force 推到目标分支。之所以用 .deploy_git 而不是直接放在 public 里：
/// public/.git 的只读对象文件会让 hexo clean 删不掉 public，导致「清理缓存」永久失败。
/// 这个目录名也和 hexo-deployer-git 保持一致，用户不会觉得多出来一个陌生东西。
/// </summary>
public sealed class GitHubPublisher : IDisposable
{
    private readonly StreamingProcess _stream = new();
    private readonly StringBuilder _currentOutput = new();

    private string _token = string.Empty;

    /// <summary>日志输出（在后台线程触发，订阅方需切回 UI 线程）。</summary>
    public event EventHandler<LogEventArgs>? OutputReceived;

    public GitHubPublisher()
    {
        _stream.Classifier = Classify;
        _stream.OutputReceived += (_, e) => Report(e.Level, Redact(e.Text));
    }

    /// <summary>用户是否点了取消。</summary>
    public bool StopRequested { get; private set; }

    public bool IsRunning => _stream.IsRunning;

    /// <summary>执行一次完整发布。</summary>
    public async Task<PublishResult> PublishAsync(PublishOptions o, CancellationToken ct = default)
    {
        _token = o.Token?.Trim() ?? string.Empty;
        StopRequested = false;

        try
        {
            Report(LogLevel.System, string.Empty);
            Report(LogLevel.System, "========== 开始发布到 GitHub Pages ==========");

            // ---------- 1. 输入校验 ----------
            if (string.IsNullOrWhiteSpace(o.BlogRoot) || !Directory.Exists(o.BlogRoot))
                return Fail("博客目录不存在。请先到「站点与目录」重新选择博客。");

            string remote = BuildPlainRemoteUrl(o);
            if (remote.Length == 0)
                return Fail("请先填写仓库（owner/repo）或自定义远程地址。");

            bool isRemoteFile = remote.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                                remote.StartsWith("\\\\", StringComparison.Ordinal);

            Report(LogLevel.Info, $"博客目录：{o.BlogRoot}");
            Report(LogLevel.Info, $"目标仓库：{remote}");
            Report(LogLevel.Info, $"发布分支：{o.Branch}");

            // ---------- 2. Git 环境 ----------
            var gitCheck = await ProcessRunner.RunAsync("git --version", o.BlogRoot,
                TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

            if (!gitCheck.Success)
            {
                return Fail(
                    "未检测到可用的 Git。\n" +
                    "一键发布依赖本机的 git 命令，请先安装 Git for Windows（https://git-scm.com/download/win），\n" +
                    "安装时保持默认选项（勾选「Git from the command line and also from 3rd-party software」），装完重开本程序。");
            }

            Report(LogLevel.Success, "Git 可用：" + FirstLine(gitCheck.CombinedOutput));

            // ---------- 3. 生成站点 ----------
            if (o.CleanBeforeBuild)
            {
                if (!await RunStreamAsync("hexo clean", o.BlogRoot, 180, ct).ConfigureAwait(false))
                    return Fail("hexo clean 执行失败，已停止发布。请查看上方红色日志。");
            }

            if (!await RunStreamAsync("hexo generate", o.BlogRoot, 600, ct).ConfigureAwait(false))
                return Fail("hexo generate 执行失败，站点没有生成成功，已停止发布。");

            // ---------- 4. 校验生成结果 ----------
            string publicDir = Path.Combine(o.BlogRoot, "public");

            if (!Directory.Exists(publicDir))
                return Fail($"没有找到 public 目录（{publicDir}）。站点可能没有生成成功。");

            if (!File.Exists(Path.Combine(publicDir, "index.html")))
                return Fail("public 目录里没有 index.html，生成结果不完整，已停止发布。");

            Report(LogLevel.Success, $"public 目录就绪：{publicDir}");

            // ---------- 5. 准备部署目录 ----------
            // 为什么不在 public 里直接 git init？
            // 踩过坑：public/.git 里的 git 对象文件是只读的，s 之后 hexo clean 想 rmdir
            // public 时会报 ENOENT / EPERM，导致「清理缓存」永远失败。
            // 所以改用与 hexo-deployer-git 一致的方案：博客根目录下的独立目录 .deploy_git，
            // 每次把 public 的内容同步进去再提交。好处是 hexo clean 完全不受影响，
            // 而且提交历史是增量的，不必每次都全量重建仓库。
            string deployDir = Path.Combine(o.BlogRoot, ".deploy_git");

            try
            {
                Directory.CreateDirectory(deployDir);
            }
            catch (Exception ex)
            {
                return Fail($"无法创建部署目录 {deployDir}：{ex.Message}");
            }

            bool hasGit = Directory.Exists(Path.Combine(deployDir, ".git"));

            if (!hasGit)
            {
                if (!await RunStreamAsync("git init", deployDir, 60, ct).ConfigureAwait(false))
                    return Fail("git init 失败。可能是博客目录没有写权限。");

                Report(LogLevel.Info, $"已在 {deployDir} 初始化部署仓库。");
            }
            else
            {
                Report(LogLevel.Info, $"复用已有的部署目录：{deployDir}");
            }

            // ---------- 6. 把 public 同步进部署目录 ----------
            int synced;
            try
            {
                synced = SyncToDeployDir(publicDir, deployDir);
                Report(LogLevel.Info, $"已同步 {synced} 个文件到部署目录。");
            }
            catch (Exception ex)
            {
                return Fail("同步生成结果到部署目录失败：" + ex.Message);
            }

            // ---------- 7. .nojekyll ----------
            if (o.WriteNoJekyll)
            {
                try
                {
                    // 内容留空即可，存在的意义只是告诉 GitHub Pages「别用 Jekyll 处理」
                    File.WriteAllText(Path.Combine(deployDir, ".nojekyll"), string.Empty);
                    Report(LogLevel.Info, "已写入 .nojekyll（跳过 Jekyll 处理，避免下划线开头的目录被忽略）。");
                }
                catch (Exception ex)
                {
                    Report(LogLevel.Warning, $"写入 .nojekyll 失败：{ex.Message}（不影响发布继续）");
                }
            }

            // ---------- 8. 暂存 + 提交 ----------
            if (!await RunStreamAsync("git -c core.quotepath=false add -A .", deployDir, 300, ct)
                    .ConfigureAwait(false))
                return Fail("git add 失败。请检查是否有文件被其他程序占用。");

            string message = BuildCommitMessage(o);
            string email = string.IsNullOrWhiteSpace(o.Owner)
                ? "hexo-blog-manager@users.noreply.github.com"
                : $"{o.Owner}@users.noreply.github.com";

            string commitCmd =
                "git -c core.quotepath=false " +
                "-c user.name=\"Hexo Blog Manager\" " +
                $"-c user.email=\"{email}\" " +
                $"commit -m \"{EscapeForCmd(message)}\"";

            bool committed = await RunStreamAsync(commitCmd, deployDir, 180, ct).ConfigureAwait(false);

            if (!committed)
            {
                // 「没有变更」不是错误：站点内容没改动时重复发布应该正常结束
                if (IsNothingToCommit())
                {
                    Report(LogLevel.Warning, "站点内容与上次发布完全一致，没有需要提交的改动。");

                    string? existing = BuildSiteUrl(o);
                    return new PublishResult
                    {
                        Success = true,
                        Message = "没有新的改动，仓库无需更新。",
                        SiteUrl = existing,
                        PagesEnabled = false
                    };
                }

                return Fail("git commit 失败。常见原因是 Git 没有配置用户名邮箱，或文件被占用。");
            }

            int fileCount = CountChangedFiles();

            // ---------- 9. 推送 ----------
            string pushUrl = isRemoteFile ? remote : BuildAuthRemoteUrl(o);

            string pushCmd = "git -c core.quotepath=false -c http.postBuffer=157286400";

            // 代理只对走 https 的远端有意义
            if (!string.IsNullOrWhiteSpace(o.Proxy) && !isRemoteFile)
                pushCmd += $" -c http.proxy=\"{o.Proxy.Trim()}\"";

            pushCmd += $" push --force \"{pushUrl}\" HEAD:refs/heads/{o.Branch}";

            Report(LogLevel.System, "$ git push --force " +
                                    (isRemoteFile ? remote : Redact(pushUrl)) +
                                    $" HEAD:refs/heads/{o.Branch}");

            if (!await RunStreamAsync(pushCmd, deployDir, 600, ct).ConfigureAwait(false))
            {
                Report(LogLevel.Error, "推送失败。");
                Report(LogLevel.System,
                    "排查方向：\n" +
                    "  · Token 是否正确、是否勾选了 repo 权限（细粒度 Token 需要 Contents: Read and write）\n" +
                    "  · 仓库是否已创建，owner/repo 拼写是否正确\n" +
                    "  · 网络能否访问 github.com（国内可能需要代理，可在「高级选项」里填写）\n" +
                    "  · 组织仓库是否开启了 SSO，需要在 Token 页面点 Authorize");
                return Fail("推送到 GitHub 失败，详情见上方日志。");
            }

            string? commitShort = await GetShortShaAsync(deployDir, ct).ConfigureAwait(false);
            Report(LogLevel.Success, $"已推送到 {o.Branch} 分支" +
                                    (commitShort is null ? "。" : $"，提交 {commitShort}。"));

            // ---------- 10. 开启 GitHub Pages ----------
            string siteUrl = BuildSiteUrl(o);
            bool pagesEnabled = false;

            if (!o.AutoEnablePages)
            {
                Report(LogLevel.Info, "已跳过自动开启 Pages（按你的设置）。请到仓库 Settings → Pages 手动选择分支。");
            }
            else if (string.IsNullOrWhiteSpace(o.Token))
            {
                Report(LogLevel.Warning, "没有填写 Token，无法自动开启 Pages。请到仓库 Settings → Pages 选择分支。");
            }
            else if (string.IsNullOrWhiteSpace(o.Owner) || string.IsNullOrWhiteSpace(o.Repo))
            {
                Report(LogLevel.Warning, "仓库信息不完整，跳过自动开启 Pages。");
            }
            else
            {
                try
                {
                    using var api = new GitHubApi(o.Token, o.Proxy);
                    var pages = await api.EnablePagesAsync(o.Owner, o.Repo, o.Branch, ct).ConfigureAwait(false);

                    pagesEnabled = pages.Ok;

                    if (pages.Ok)
                    {
                        Report(LogLevel.Success, pages.Message);
                        if (!string.IsNullOrWhiteSpace(pages.Url))
                            siteUrl = pages.Url;
                    }
                    else
                    {
                        Report(LogLevel.Warning, pages.Message);
                        Report(LogLevel.System,
                            "这不影响代码已经推送成功。你也可以手动开启：\n" +
                            $"  仓库 → Settings → Pages → Source 选择「{o.Branch}」分支 → Save");
                    }
                }
                catch (GitHubApiException ex)
                {
                    Report(LogLevel.Warning, ex.Message);
                    Report(LogLevel.System,
                        "推送本身已经成功，只是「自动开启 Pages」这一步没成功。手动开启路径：\n" +
                        $"  仓库 → Settings → Pages → Source 选择「{o.Branch}」分支 → Save");
                }
            }

            // ---------- 11. 收尾 ----------
            Report(LogLevel.System, string.Empty);
            Report(LogLevel.Success, "========== 发布完成 ==========");
            Report(LogLevel.Info, $"站点地址：{siteUrl}");
            Report(LogLevel.Info, "首次开启 Pages 或刚推送完，GitHub 需要 1~2 分钟构建，期间访问 404 属正常。");

            return new PublishResult
            {
                Success = true,
                Message = "发布成功，代码已推送到 GitHub。",
                SiteUrl = siteUrl,
                CommitShort = commitShort,
                PagesEnabled = pagesEnabled,
                FileCount = fileCount
            };
        }
        catch (OperationCanceledException)
        {
            Report(LogLevel.Warning, "发布已取消。");
            return Fail("发布已取消。");
        }
        catch (Exception ex)
        {
            Report(LogLevel.Error, $"发布过程中出现异常：{ex.Message}");
            return Fail($"发布失败：{ex.Message}");
        }
    }

    /// <summary>停止当前操作。</summary>
    public void Stop()
    {
        StopRequested = true;
        _stream.Stop();
    }

    // ==================================================================
    // 命令执行
    // ==================================================================

    /// <summary>执行一条命令并等待结束；返回是否成功（退出码 0 且未超时）。</summary>
    private async Task<bool> RunStreamAsync(string command, string workDir, int timeoutSeconds, CancellationToken ct)
    {
        Report(LogLevel.System, "$ " + Redact(command));
        _currentOutput.Clear();

        if (!_stream.Start(command, workDir, out string error))
        {
            Report(LogLevel.Error, error);
            return false;
        }

        var (code, timedOut) = await _stream.WaitForExitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct)
            .ConfigureAwait(false);

        if (timedOut)
        {
            Report(LogLevel.Error, $"命令执行超过 {timeoutSeconds} 秒，已强制终止。");
            return false;
        }

        if (code != 0)
        {
            Report(LogLevel.Error, $"命令退出码 {code}。");
            return false;
        }

        return true;
    }

    private async Task<string?> GetShortShaAsync(string workDir, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("git rev-parse --short HEAD", workDir,
            TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

        return result.Success ? FirstLine(result.StdOut) : null;
    }

    /// <summary>从 git 输出里粗略统计变更文件数量（"N files changed"）。</summary>
    private int CountChangedFiles()
    {
        string text = _currentOutput.ToString();
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\s+files?\s+changed");
        return match.Success && int.TryParse(match.Groups[1].Value, out int n) ? n : 0;
    }

    private bool IsNothingToCommit()
    {
        string text = _currentOutput.ToString().ToLowerInvariant();
        return text.Contains("nothing to commit") ||
               text.Contains("no changes added to commit") ||
               text.Contains("working tree clean");
    }

    /// <summary>
    /// 把生成好的 public 目录同步到部署目录（保留部署目录里的 .git）。
    ///
    /// 做法：
    /// 1) 先删掉部署目录里「源已经不存在」的文件，避免旧文章页面残留在站点上；
    /// 2) 再把源文件全量覆盖过去。
    ///
    /// 为什么不用「清空再复制」？大博客几千个文件时，全删全拷很慢；
    /// 增量清理只动差异部分，快得多。
    /// </summary>
    private static int SyncToDeployDir(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        // 1. 收集源文件相对路径
        //    注意要排除源目录里可能残留的 .git（老版本工具或用户自己 git init 过 public），
        //    否则会把 git 对象文件也当成站点内容拷过去，覆盖部署仓库的 .git 时报「拒绝访问」。
        var sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);

            if (IsInsideGitDir(relative))
                continue;

            sourceFiles.Add(relative);
        }

        // 2. 清理目标中多余的条目（.git 一律跳过）
        foreach (string file in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories).ToList())
        {
            string relative = Path.GetRelativePath(targetDir, file);

            if (IsInsideGitDir(relative))
                continue;

            if (sourceFiles.Contains(relative))
                continue;

            try
            {
                File.SetAttributes(file, FileAttributes.Normal);   // git 对象是只读的，先去掉
                File.Delete(file);
            }
            catch { /* 个别文件删不掉不影响整体，继续 */ }
        }

        // 3. 清掉空目录（从深到浅处理，否则删不干净）
        //    跳过 .git 内部的任何目录：刚 init 完的 .git/objects、.git/refs 都是空的，
        //    如果不跳过会被当成「空目录」删掉，仓库直接变成 not a git repository。
        var emptyCandidates = Directory.EnumerateDirectories(targetDir, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length)
            .ToList();

        foreach (string dir in emptyCandidates)
        {
            if (IsInsideGitDir(Path.GetRelativePath(targetDir, dir)))
                continue;

            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { }
        }

        // 4. 复制最新文件
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);

            if (IsInsideGitDir(relative))
                continue;

            string destination = Path.Combine(targetDir, relative);

            string? parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            // 目标文件可能是上一次留下的只读文件（例如 .nojekyll 曾被设成只读），先去掉属性再覆盖
            if (File.Exists(destination))
            {
                try { File.SetAttributes(destination, FileAttributes.Normal); } catch { }
            }

            File.Copy(file, destination, overwrite: true);
        }

        return sourceFiles.Count;
    }

    /// <summary>判断相对路径是否落在 .git 目录里（任意层级出现 .git 段都算）。</summary>
    private static bool IsInsideGitDir(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return false;

        foreach (string segment in relativePath.Split(
                     new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // ==================================================================
    // 地址与文本处理
    // ==================================================================

    /// <summary>不带 Token 的远程地址。优先用自定义地址，否则按 owner/repo 拼 GitHub 地址。</summary>
    public static string BuildPlainRemoteUrl(PublishOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.RemoteUrl))
            return o.RemoteUrl.Trim();

        if (string.IsNullOrWhiteSpace(o.Owner) || string.IsNullOrWhiteSpace(o.Repo))
            return string.Empty;

        return $"https://github.com/{o.Owner.Trim()}/{o.Repo.Trim()}.git";
    }

    /// <summary>
    /// 带上 Token 的推送地址。Token 用 x-access-token 用户名传递，
    /// 这是 GitHub 官方支持的方式，比把 Token 当密码更明确。
    /// </summary>
    private string BuildAuthRemoteUrl(PublishOptions o)
    {
        string url = BuildPlainRemoteUrl(o);

        if (string.IsNullOrWhiteSpace(_token))
            return url;

        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;   // ssh / file 地址不加 Token

        if (url.Contains("x-access-token:", StringComparison.OrdinalIgnoreCase))
            return url;

        return "https://x-access-token:" + Uri.EscapeDataString(_token) + "@" + url[8..];
    }

    /// <summary>
    /// 站点访问地址。
    /// 只要填了 owner/repo 就算得出来，与远程地址是不是 GitHub 无关
    /// （有人用 SSH 或自建远端推同一个仓库，站点地址依然是 github.io 那个）。
    /// </summary>
    private static string BuildSiteUrl(PublishOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Owner) || string.IsNullOrWhiteSpace(o.Repo))
            return string.Empty;

        return GitHubApi.BuildPagesUrl(o.Owner, o.Repo);
    }

    /// <summary>把提交信息里的 {date} 换成当前时间。</summary>
    private static string BuildCommitMessage(PublishOptions o)
    {
        string template = string.IsNullOrWhiteSpace(o.CommitMessage)
            ? "Site updated: {date}"
            : o.CommitMessage.Trim();

        return template.Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    /// <summary>提交信息里不能有双引号，会被 cmd 提前截断。</summary>
    private static string EscapeForCmd(string text) => text.Replace("\"", "'");

    /// <summary>日志脱敏：任何位置出现的 Token 一律替换成 ***。</summary>
    private string Redact(string text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_token))
            return text;

        string result = text.Replace(_token, "***", StringComparison.Ordinal);

        string encoded = Uri.EscapeDataString(_token);
        if (encoded != _token)
            result = result.Replace(encoded, "***", StringComparison.Ordinal);

        return result;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return line.Trim();
    }

    // ==================================================================
    // 日志
    // ==================================================================

    private void Report(LogLevel level, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            OutputReceived?.Invoke(this, new LogEventArgs(level, string.Empty));
            return;
        }

        // 记录原始（脱敏后）输出，供后面判断 "nothing to commit" 之类的关键字
        _currentOutput.AppendLine(text);
        OutputReceived?.Invoke(this, new LogEventArgs(level, text));
    }

    /// <summary>git / hexo 混合的分级规则。</summary>
    private static LogLevel Classify(string line)
    {
        string t = AnsiText.Strip(line).TrimStart();

        if (t.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("FATAL", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;

        if (t.StartsWith("WARN", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("warning:", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("remote: warning", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warning;

        if (t.StartsWith("INFO", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Info;

        return LogLevel.Info;
    }

    private static PublishResult Fail(string message)
    {
        return new PublishResult { Success = false, Message = message };
    }

    // ==================================================================
    // 输入解析
    // ==================================================================

    /// <summary>
    /// 解析用户填的仓库输入，支持这几种写法：
    /// owner/repo、https://github.com/owner/repo（可带 .git）、git@github.com:owner/repo.git
    /// </summary>
    public static (string Owner, string Repo) TryParseRepoInput(string? input)
    {
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return (string.Empty, string.Empty);

        // git@github.com:owner/repo.git
        if (text.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            int colon = text.IndexOf(':');
            if (colon > 0 && colon < text.Length - 1)
                text = text[(colon + 1)..];
        }
        else if (text.Contains("://", StringComparison.Ordinal))
        {
            try { text = new Uri(text).AbsolutePath; } catch { }
        }

        text = text.Trim('/').Trim();

        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            text = text[..^4];

        string[] parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2
            ? (parts[^2], parts[^1])
            : (string.Empty, string.Empty);
    }

    public void Dispose() => _stream.Dispose();
}
