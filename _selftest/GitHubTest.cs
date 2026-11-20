using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HexoBlogManager.Services;

namespace SelfTest;

/// <summary>
/// 一键发布模块的自检。
/// 覆盖三块：
/// 1) 纯函数（仓库输入解析、站点地址推算、DPAPI 加解密）；
/// 2) GitHub API 客户端（对着本地 mock 服务跑，不需要真实网络和 Token）；
/// 3) 完整发布流程（用本地 bare 仓库当远端，真实跑 hexo generate + git 推送）。
/// </summary>
internal static class GitHubTests
{
    private const string BlogRoot = @"D:\testblog";
    private const string BareRepo = @"D:\testpage.git";

    public static (int Pass, int Fail) Run()
    {
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            if (ok)
            {
                pass++;
                Console.WriteLine($"[PASS] {name}");
            }
            else
            {
                fail++;
                Console.WriteLine($"[FAIL] {name}" + (detail is null ? "" : $"  → 实际：{detail}"));
            }
        }

        // ================= 1. 输入解析 =================
        var p1 = GitHubPublisher.TryParseRepoInput("octocat/my-blog");
        Check("解析 owner/repo", p1.Owner == "octocat" && p1.Repo == "my-blog", $"{p1.Owner}|{p1.Repo}");

        var p2 = GitHubPublisher.TryParseRepoInput("https://github.com/octocat/my-blog.git");
        Check("解析完整 HTTPS 地址", p2.Owner == "octocat" && p2.Repo == "my-blog", $"{p2.Owner}|{p2.Repo}");

        var p3 = GitHubPublisher.TryParseRepoInput("git@github.com:octocat/my-blog.git");
        Check("解析 SSH 地址", p3.Owner == "octocat" && p3.Repo == "my-blog", $"{p3.Owner}|{p3.Repo}");

        var p4 = GitHubPublisher.TryParseRepoInput("  https://github.com/octocat/my-blog/  ");
        Check("解析带空格与尾斜杠的地址", p4.Owner == "octocat" && p4.Repo == "my-blog", $"{p4.Owner}|{p4.Repo}");

        var p5 = GitHubPublisher.TryParseRepoInput("");
        Check("空输入返回空", p5.Owner.Length == 0 && p5.Repo.Length == 0, $"{p5.Owner}|{p5.Repo}");

        // ================= 2. 站点地址推算 =================
        Check("用户主页仓库 → 根域名",
            GitHubApi.BuildPagesUrl("octocat", "octocat.github.io") == "https://octocat.github.io/",
            GitHubApi.BuildPagesUrl("octocat", "octocat.github.io"));

        Check("普通仓库 → 子路径",
            GitHubApi.BuildPagesUrl("octocat", "my-blog") == "https://octocat.github.io/my-blog/",
            GitHubApi.BuildPagesUrl("octocat", "my-blog"));

        // ================= 3. DPAPI 加解密 =================
        string cipher = SecretProtector.Protect("ghp_selftest_123456");
        Check("Token 加密后不是明文", cipher.Length > 0 && !cipher.Contains("ghp_selftest"), cipher);
        Check("Token 可正确解密", SecretProtector.Unprotect(cipher) == "ghp_selftest_123456",
            SecretProtector.Unprotect(cipher));
        Check("损坏密文返回空串（不崩溃）", SecretProtector.Unprotect("not-base64!!!") == string.Empty);
        Check("空值加解密安全", SecretProtector.Protect("") == string.Empty);

        // ================= 4. GitHub API 客户端（mock 服务） =================
        using (var server = new MockGitHubServer("ghp_selftest_123456"))
        {
            GitHubApi.BaseUrl = server.Url;

            using var api = new GitHubApi("ghp_selftest_123456");

            string login = api.GetLoginAsync().GetAwaiter().GetResult();
            Check("Token 校验返回登录名", login == "selftest-user", login);

            var notExist = api.GetRepoStatusAsync("selftest-user", "my-blog").GetAwaiter().GetResult();
            Check("仓库不存在时返回 Exists=false", !notExist.Exists);

            api.CreateRepoAsync("my-blog").GetAwaiter().GetResult();
            var created = api.GetRepoStatusAsync("selftest-user", "my-blog").GetAwaiter().GetResult();
            Check("创建仓库后可以查到", created.Exists && created.FullName == "selftest-user/my-blog",
                created.FullName);
            Check("新仓库 Pages 未开启", !created.PagesEnabled);

            var enabled = api.EnablePagesAsync("selftest-user", "my-blog", "gh-pages").GetAwaiter().GetResult();
            Check("开启 Pages 成功", enabled.Ok, enabled.Message);
            Check("Pages 地址正确", enabled.Url == "https://selftest-user.github.io/my-blog/", enabled.Url);

            var afterEnable = api.GetRepoStatusAsync("selftest-user", "my-blog").GetAwaiter().GetResult();
            Check("Pages 状态回读为已开启", afterEnable.PagesEnabled && afterEnable.PagesBranch == "gh-pages",
                $"enabled={afterEnable.PagesEnabled}, branch={afterEnable.PagesBranch}");

            var again = api.EnablePagesAsync("selftest-user", "my-blog", "gh-pages").GetAwaiter().GetResult();
            Check("分支一致时不重复创建", again.Ok && again.Message.Contains("无需改动"), again.Message);

            var switched = api.EnablePagesAsync("selftest-user", "my-blog", "docs").GetAwaiter().GetResult();
            Check("分支不一致时走切换（PUT）", switched.Ok && server.PagesBranch == "docs", server.PagesBranch);

            // 错误 Token
            try
            {
                using var badApi = new GitHubApi("wrong-token");
                badApi.GetLoginAsync().GetAwaiter().GetResult();
                Check("错误 Token 应抛异常", false, "没有抛异常");
            }
            catch (GitHubApiException ex)
            {
                Check("错误 Token 抛 401 并给出中文提示",
                    ex.StatusCode == 401 && ex.Message.Contains("Token"), $"{ex.StatusCode}:{ex.Message}");
            }
        }

        // ================= 5. 完整发布流程（本地 bare 仓库当远端） =================
        try
        {
            if (Directory.Exists(BareRepo))
                ForceDelete(BareRepo);

            var init = ProcessRunner.RunAsync($"git init --bare \"{BareRepo}\"",
                Path.GetPathRoot(BareRepo), TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Check("准备本地 bare 仓库", init.Success, init.CombinedOutput);

            var logs = new List<string>();
            using var publisher = new GitHubPublisher();
            publisher.OutputReceived += (_, e) =>
            {
                lock (logs) logs.Add($"{e.Level}|{e.Text}");
            };

            var options = new PublishOptions
            {
                BlogRoot = BlogRoot,
                Owner = "selftest-user",
                Repo = "my-blog",
                Branch = "gh-pages",
                CommitMessage = "selftest publish {date}",
                Token = string.Empty,          // 本地 file 远端不需要 Token
                RemoteUrl = "file:///" + BareRepo.Replace('\\', '/'),
                CleanBeforeBuild = false,      // 复用已有 public，省时间
                WriteNoJekyll = true,
                AutoEnablePages = false        // file 远端跳过 API
            };

            var result = publisher.PublishAsync(options).GetAwaiter().GetResult();
            Check("发布流程返回成功", result.Success, result.Message);
            Check("拿到提交 SHA", !string.IsNullOrWhiteSpace(result.CommitShort), result.CommitShort);

            // 远端分支真的有了
            var branchCheck = ProcessRunner.RunAsync("git rev-parse --verify gh-pages",
                BareRepo, TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            Check("远端存在 gh-pages 分支", branchCheck.Success, branchCheck.CombinedOutput);

            // 内容正确
            var tree = ProcessRunner.RunAsync("git ls-tree --name-only gh-pages",
                BareRepo, TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            Check("远端包含 index.html", tree.StdOut.Contains("index.html"), tree.StdOut.Trim());
            Check("远端包含 .nojekyll", tree.StdOut.Contains(".nojekyll"), tree.StdOut.Trim());
            Check("远端包含静态资源目录", tree.StdOut.Contains("css") || tree.StdOut.Contains("js"),
                tree.StdOut.Trim());

            // 部署目录：public 里不应该有 .git（有它会挡住 hexo clean）
            string deployDir = Path.Combine(BlogRoot, ".deploy_git");
            Check("已创建独立部署目录 .deploy_git", Directory.Exists(deployDir), deployDir);

            var deployHead = ProcessRunner.RunAsync("git rev-parse --verify HEAD",
                deployDir, TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            Check("部署仓库本体完好（.git 没被同步过程污染）", deployHead.Success, deployHead.CombinedOutput);

            // 生成结果里的中文文件名/目录不乱码（Windows 上最容易踩的坑）
            var files = ProcessRunner.RunAsync("git ls-files", deployDir,
                TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            Check("部署目录的文件清单非空", files.StdOut.Trim().Length > 0);
            Check("中文分类目录已进入仓库", files.StdOut.Contains("categories/"), files.StdOut.Length.ToString());

            // 重复发布：Butterfly 的标签云顺序、lastPushDate 每次生成都会变，
            // 所以真实博客重复发布会一直有 diff。这里只验证 force push 依旧有效、不报错。
            var second = publisher.PublishAsync(options).GetAwaiter().GetResult();
            Check("重复发布不报错", second.Success, second.Message);

            var branchCheck2 = ProcessRunner.RunAsync("git rev-parse --verify gh-pages",
                BareRepo, TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            Check("重复发布后远端分支仍然有效", branchCheck2.Success, branchCheck2.CombinedOutput);

            // 回归：发布之后 hexo clean 必须还能正常执行（之前 .git 放在 public 里会卡死）
            var clean = ProcessRunner.RunAsync("hexo clean", BlogRoot, TimeSpan.FromSeconds(180))
                .GetAwaiter().GetResult();
            Check("发布后 hexo clean 仍然成功", clean.Success, clean.CombinedOutput);
            Check("clean 不会破坏部署目录", Directory.Exists(Path.Combine(deployDir, ".git")));

            // Token 脱敏：日志里不应该出现 Token 原文
            lock (logs)
            {
                Check("日志没有泄露 Token", logs.All(l => !l.Contains("ghp_")));
                Check("日志包含命令回显", logs.Any(l => l.Contains("git push")), logs.Count.ToString());
            }
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("[FAIL] 发布流程异常：" + ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(BareRepo))
                    ForceDelete(BareRepo);
            }
            catch { }
        }

        // ================= 6. 「没有改动」分支 =================
        // 用一个输出稳定的假 hexo（每次生成的文件内容完全一致），
        // 验证第二次发布会走「nothing to commit → 正常结束」而不是报错。
        RunNoChangeScenario(Check);

        return (pass, fail);
    }

    /// <summary>
    /// 递归删除目录。
    /// git 的对象文件是只读的，直接 Directory.Delete 会抛 0x80070005（拒绝访问），
    /// 所以先把只读属性清掉。
    /// </summary>
    private static void ForceDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
        }
        catch { }

        Directory.Delete(dir, recursive: true);
    }

    private static void RunNoChangeScenario(Action<string, bool, string?> check)
    {
        const string fakeBlog = @"D:\selftest-fakeblog";
        const string fakeBin = @"D:\selftest-fakebin";
        const string bare = @"D:\selftest-fake.git";

        string? oldPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            if (Directory.Exists(fakeBlog)) ForceDelete(fakeBlog);
            if (Directory.Exists(fakeBin)) ForceDelete(fakeBin);
            if (Directory.Exists(bare)) ForceDelete(bare);

            Directory.CreateDirectory(Path.Combine(fakeBlog, "source", "_posts"));
            Directory.CreateDirectory(fakeBin);

            File.WriteAllText(Path.Combine(fakeBlog, "_config.yml"), "title: 稳定输出测试\ntheme: butterfly\n");

            // 假 hexo：generate 永远产出同一份 index.html，clean 删掉 public
            File.WriteAllText(Path.Combine(fakeBin, "hexo.cmd"),
                "@echo off\r\n" +
                "if /i \"%1\"==\"generate\" goto gen\r\n" +
                "if /i \"%1\"==\"clean\" goto clean\r\n" +
                "exit /b 0\r\n" +
                ":gen\r\n" +
                "if not exist \"public\" mkdir \"public\"\r\n" +
                "> \"public\\index.html\" echo ^<html^>^<body^>stable^</body^>^</html^>\r\n" +
                "exit /b 0\r\n" +
                ":clean\r\n" +
                "if exist \"public\" rmdir /s /q \"public\"\r\n" +
                "exit /b 0\r\n");

            ProcessRunner.RunAsync($"git init --bare \"{bare}\"",
                Path.GetPathRoot(bare)!, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

            // 把假 hexo 放到 PATH 最前面，子进程 cmd 会优先命中它
            Environment.SetEnvironmentVariable("PATH", fakeBin + ";" + oldPath);

            using var publisher = new GitHubPublisher();
            var options = new PublishOptions
            {
                BlogRoot = fakeBlog,
                Branch = "gh-pages",
                CommitMessage = "stable {date}",
                RemoteUrl = "file:///" + bare.Replace('\\', '/'),
                // 关掉 clean 是为了第二次发布复用同一份 public（更快），
                // 顺带验证「内容没变 → nothing to commit」这条分支。
                CleanBeforeBuild = false,
                WriteNoJekyll = true,
                AutoEnablePages = false
            };

            var first = publisher.PublishAsync(options).GetAwaiter().GetResult();
            check("假博客首次发布成功", first.Success, first.Message);

            var second = publisher.PublishAsync(options).GetAwaiter().GetResult();
            check("内容无变化时第二次发布不报错", second.Success, second.Message);
            check("内容无变化时提示没有新的改动", second.Message.Contains("没有新的改动"), second.Message);

            // 远端分支没有被破坏
            var verify = ProcessRunner.RunAsync("git rev-parse --verify gh-pages",
                bare, TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            check("无改动时远端分支仍然有效", verify.Success, verify.CombinedOutput);

            // 打开 clean 再发一次：public 会被整个删除重建，应当仍然成功
            options.CleanBeforeBuild = true;
            var third = publisher.PublishAsync(options).GetAwaiter().GetResult();
            check("开启 clean 后发布仍然成功", third.Success, third.Message);
        }
        catch (Exception ex)
        {
            check("无改动场景执行", false, ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);

            foreach (string dir in new[] { fakeBlog, fakeBin, bare })
            {
                try { if (Directory.Exists(dir)) ForceDelete(dir); } catch { }
            }
        }
    }
}

/// <summary>
/// 极简 GitHub API mock 服务。
/// 用 TcpListener 手写 HTTP/1.1，而不是 HttpListener —— 后者在 Windows 上需要
/// http.sys 的 URL ACL 授权，非管理员跑不起来。
/// </summary>
public sealed class MockGitHubServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public MockGitHubServer(string token)
    {
        Token = token;

        // 端口从 5599 往上找，避免撞车
        TcpListener? listener = null;
        for (int port = 5599; port < 5620; port++)
        {
            try
            {
                var candidate = new TcpListener(IPAddress.Loopback, port);
                candidate.Start();
                listener = candidate;
                Url = $"http://127.0.0.1:{port}/";
                break;
            }
            catch { }
        }

        if (listener is null)
            throw new InvalidOperationException("找不到可用端口启动 mock 服务。");

        _listener = listener;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string Url { get; } = string.Empty;
    public string Token { get; }

    public bool RepoExists { get; private set; }
    public bool PagesEnabled { get; private set; }
    public string PagesBranch { get; private set; } = string.Empty;
    public bool RepoPrivate { get; set; }

    /// <summary>收到过的请求（"METHOD /path"），便于断言调用次数。</summary>
    public List<string> Requests { get; } = new();

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                string? requestText = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (requestText is null) return;

                string[] lines = requestText.Split('\n');
                string requestLine = lines[0].Trim();
                string[] parts = requestLine.Split(' ');
                if (parts.Length < 2) return;

                string method = parts[0].ToUpperInvariant();
                string path = parts[1];
                if (path.Contains('?')) path = path[..path.IndexOf('?')];

                string auth = string.Empty;
                foreach (var line in lines)
                {
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                        auth = line["Authorization:".Length..].Trim();
                }

                lock (Requests)
                    Requests.Add($"{method} {path}");

                var (status, body) = Route(method, path, auth, requestText);
                await WriteResponseAsync(stream, status, body).ConfigureAwait(false);
            }
        }
        catch
        {
            // mock 服务出错不影响测试继续
        }
    }

    private (int Status, string Body) Route(string method, string path, string auth, string rawRequest)
    {
        // 鉴权：除了创建仓库/仓库查询，其余都要求 Bearer
        bool authorized = auth.Equals("Bearer " + Token, StringComparison.Ordinal);

        if (path == "/user" && method == "GET")
        {
            if (!authorized) return (401, "{\"message\":\"Bad credentials\"}");
            return (200, "{\"login\":\"selftest-user\",\"id\":1}");
        }

        if (path == "/user/repos" && method == "POST")
        {
            if (!authorized) return (401, "{\"message\":\"Bad credentials\"}");
            RepoExists = true;
            return (201, "{\"full_name\":\"selftest-user/my-blog\"}");
        }

        // /repos/{owner}/{repo}
        string[] seg = path.Trim('/').Split('/');
        if (seg.Length == 3 && seg[0] == "repos" && method == "GET")
        {
            if (!authorized) return (401, "{\"message\":\"Bad credentials\"}");
            if (!RepoExists) return (404, "{\"message\":\"Not Found\"}");

            return (200, JsonSerializer.Serialize(new
            {
                full_name = $"{seg[1]}/{seg[2]}",
                @private = RepoPrivate,
                default_branch = "main",
                html_url = $"https://github.com/{seg[1]}/{seg[2]}"
            }));
        }

        // /repos/{owner}/{repo}/pages
        if (seg.Length == 4 && seg[0] == "repos" && seg[3] == "pages")
        {
            if (!authorized) return (401, "{\"message\":\"Bad credentials\"}");

            if (method == "GET")
            {
                if (!PagesEnabled) return (404, "{\"message\":\"Not Found\"}");
                return (200, JsonSerializer.Serialize(new
                {
                    html_url = $"https://{seg[1]}.github.io/{seg[2]}/",
                    status = "built",
                    source = new { branch = PagesBranch, path = "/" }
                }));
            }

            if (method is "POST" or "PUT")
            {
                string branch = ExtractBranch(rawRequest);
                PagesEnabled = true;
                PagesBranch = branch;

                if (method == "PUT")
                    return (204, string.Empty);

                return (201, JsonSerializer.Serialize(new
                {
                    html_url = $"https://{seg[1]}.github.io/{seg[2]}/"
                }));
            }
        }

        return (404, "{\"message\":\"Not Found\"}");
    }

    /// <summary>从请求体里粗暴地取出 "branch":"xxx"。</summary>
    private static string ExtractBranch(string raw)
    {
        int idx = raw.IndexOf("\"branch\"", StringComparison.Ordinal);
        if (idx < 0) return string.Empty;

        int colon = raw.IndexOf(':', idx);
        int firstQuote = raw.IndexOf('"', colon + 1);
        int secondQuote = raw.IndexOf('"', firstQuote + 1);
        if (colon < 0 || firstQuote < 0 || secondQuote < 0) return string.Empty;

        return raw[(firstQuote + 1)..secondQuote];
    }

    private static async Task<string?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        int contentLength = 0;

        // 先读头部
        while (true)
        {
            int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read <= 0) break;

            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));

            string text = sb.ToString();
            int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd >= 0)
            {
                foreach (var line in text[..headerEnd].Split('\n'))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(line["Content-Length:".Length..].Trim(), out int len))
                        contentLength = len;
                }

                // 有 body 就继续读够
                while (text.Length - (headerEnd + 4) < contentLength)
                {
                    int more = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (more <= 0) break;
                    text += Encoding.UTF8.GetString(buffer, 0, more);
                }

                return text;
            }

            if (sb.Length > 65536) break;
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);

        string head =
            $"HTTP/1.1 {status} {StatusText(status)}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.UTF8.GetBytes(head)).ConfigureAwait(false);
        if (bytes.Length > 0)
            await stream.WriteAsync(bytes).ConfigureAwait(false);

        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        401 => "Unauthorized",
        404 => "Not Found",
        _ => "OK"
    };

    /// <summary>前台常驻运行（供 --serve 使用，给界面截图测试提供 API）。</summary>
    public void RunForever()
    {
        Console.WriteLine("Mock GitHub API 已启动：" + Url);
        Console.WriteLine("Token：" + Token);
        Thread.Sleep(Timeout.Infinite);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
