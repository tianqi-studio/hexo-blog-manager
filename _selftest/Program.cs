using System.IO;
using HexoBlogManager.Models;
using HexoBlogManager.Services;

// ============================================================
// 模块二核心逻辑自检：front-matter 解析、保真重建、新建、保存
// 直接在真实测试博客 D:\testblog 上跑，跑完删除临时文章。
// ============================================================

const string BlogRoot = @"D:\testblog";

// 让控制台按 UTF-8 输出，避免中文在管道里乱码
Console.OutputEncoding = System.Text.Encoding.UTF8;

// ---------- 辅助模式（给界面截图验证用） ----------
// --encrypt <明文>：输出 DPAPI 密文，可写进 settings.json 让主程序解密
if (args.Length >= 2 && args[0] == "--encrypt")
{
    Console.Write(SecretProtector.Protect(args[1]));
    return 0;
}

// --serve：前台常驻一个 mock GitHub API，供主程序用 --api-base 指向
if (args.Length >= 1 && args[0] == "--serve")
{
    using var mockServer = new SelfTest.MockGitHubServer("ghp_selftest_123456");
    mockServer.RunForever();
    return 0;
}

int pass = 0;
int fail = 0;

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

var service = new PostService(BlogRoot);
var posts = service.LoadAll();

Check("扫描文章数量 = 4", posts.Count == 4, posts.Count.ToString());

var guide = posts.FirstOrDefault(p => p.FileName == "butterfly-guide.md");
if (guide is null)
{
    Console.WriteLine("[FAIL] 找不到 butterfly-guide.md，后续测试中止");
    return 1;
}

// ---------- 解析 ----------
Check("title 解析", guide.Title == "Butterfly 主题上手指南", guide.Title);
Check("tags 解析（多行列表）",
    guide.Tags.SequenceEqual(new[] { "Butterfly", "Hexo", "教程" }),
    string.Join("|", guide.Tags));
Check("categories 解析", guide.Categories.SequenceEqual(new[] { "建站笔记" }),
    string.Join("|", guide.Categories));
Check("top: true → 置顶", guide.Top);
Check("comments: false → 关闭评论", !guide.Comments);
Check("date 解析", guide.Date?.ToString("yyyy-MM-dd HH:mm:ss") == "2026-11-18 09:30:00",
    guide.Date?.ToString("yyyy-MM-dd HH:mm:ss"));
Check("updated 解析", guide.Updated?.ToString("yyyy-MM-dd HH:mm:ss") == "2026-11-20 14:05:00",
    guide.Updated?.ToString("yyyy-MM-dd HH:mm:ss"));

// 行内数组写法（windows-path-tips 用的是 tags: [Windows, 踩坑记录]）
var tips = posts.FirstOrDefault(p => p.FileName == "windows-path-tips.md");
Check("行内数组 tags 解析",
    tips is not null && tips.Tags.SequenceEqual(new[] { "Windows", "踩坑记录" }),
    tips is null ? "null" : string.Join("|", tips.Tags));

// 数字形式置顶（draft-notes 用的是 top: 2）
var draft = posts.FirstOrDefault(p => p.FileName == "draft-notes.md");
Check("top: 2 → 置顶", draft is not null && draft.Top);
Check("单值 categories 解析", draft is not null && draft.Categories.SequenceEqual(new[] { "未分类" }),
    draft is null ? "null" : string.Join("|", draft.Categories));

// ---------- 保真重建（最关键：不能丢用户自定义字段） ----------
string rebuilt = FrontMatter.Build(guide, guide.FrontMatter) + guide.Body;

Check("重建保留 cover 字段", rebuilt.Contains("cover: /images/butterfly-cover.png"));
Check("重建保留 description 字段", rebuilt.Contains("description:"));
Check("重建保留正文", rebuilt.Contains("## 为什么选 Butterfly"));
Check("重建后 title 正确", rebuilt.Contains("title: Butterfly 主题上手指南"));
Check("重建后 top 为 true", rebuilt.Contains("top: true"));
Check("重建后 comments 为 false", rebuilt.Contains("comments: false"));

var reparsed = FrontMatter.Parse(rebuilt);
Check("重建结果可再次解析", FrontMatter.GetScalar(reparsed, "title") == guide.Title,
    FrontMatter.GetScalar(reparsed, "title"));
Check("重建后 tags 仍为 3 个", FrontMatter.GetList(reparsed, "tags").Count == 3,
    FrontMatter.GetList(reparsed, "tags").Count.ToString());
Check("重建后 cover 仍可读", FrontMatter.GetScalar(reparsed, "cover") == "/images/butterfly-cover.png",
    FrontMatter.GetScalar(reparsed, "cover"));

// ---------- 新建 ----------
var newMeta = new PostMeta
{
    Title = "临时测试文章",
    Date = new DateTime(2026, 11, 20, 21, 0, 0),
    Updated = new DateTime(2026, 11, 20, 21, 0, 0),
    Tags = new List<string> { "标签A", "标签B" },
    Categories = new List<string> { "测试分类" },
    Top = false,
    Comments = true
};

string newPath = service.CreateNew("selftest-temp", newMeta, "这是正文第一行。\n");
Check("新建文件已生成", File.Exists(newPath), newPath);

string created = File.ReadAllText(newPath);
Check("新建含 title", created.Contains("title: 临时测试文章"));
Check("新建含 date", created.Contains("date: 2026-11-20 21:00:00"));
Check("新建含 tags 列表项", created.Contains("  - 标签A") && created.Contains("  - 标签B"));
Check("新建含 categories", created.Contains("  - 测试分类"));
Check("新建含 top: false", created.Contains("top: false"));
Check("新建含 comments: true", created.Contains("comments: true"));
Check("新建含正文", created.Contains("这是正文第一行。"));
Check("新建以 --- 结尾 front-matter", created.StartsWith("---\n") && created.Contains("\n---\n"));

// ---------- 修改并保存 ----------
var loaded = service.ReadPost(newPath);
loaded.Top = true;
loaded.Comments = false;
loaded.Tags.Add("标签C");
service.Save(loaded, "更新后的正文。\n");

string updated = File.ReadAllText(newPath);
Check("保存后 top: true", updated.Contains("top: true"));
Check("保存后 comments: false", updated.Contains("comments: false"));
Check("保存后新增标签", updated.Contains("  - 标签C"));
Check("保存后正文更新", updated.Contains("更新后的正文。"));
Check("保存后旧正文已移除", !updated.Contains("这是正文第一行。"));

var reload = service.ReadPost(newPath);
Check("保存后可重新读取", reload.Tags.Count == 3 && reload.Top && !reload.Comments,
    $"tags={reload.Tags.Count}, top={reload.Top}, comments={reload.Comments}");

// ---------- 文件名冲突 ----------
var dupMeta = new PostMeta { Title = "重名测试", Date = DateTime.Now, Updated = DateTime.Now };
string dupPath = service.CreateNew("selftest-temp", dupMeta, "x");
Check("重名自动加后缀", Path.GetFileName(dupPath) == "selftest-temp-1.md", Path.GetFileName(dupPath));

// ---------- 目录校验 ----------
var siteService = new BlogSiteService();
var okResult = siteService.Validate(BlogRoot);
Check("合法目录校验通过", okResult.IsValid && okResult.Site?.Theme == "butterfly",
    okResult.Message);

var badResult = siteService.Validate(Path.Combine(BlogRoot, "source"));
Check("source 子目录被识别为选错层级", !badResult.IsValid && badResult.Message.Contains("子目录"),
    badResult.Message);

var missingResult = siteService.Validate(@"D:\definitely-not-exist-12345");
Check("不存在的目录被拒绝", !missingResult.IsValid && missingResult.Message.Contains("不存在"),
    missingResult.Message);

// ---------- 清理 ----------
try
{
    File.Delete(newPath);
    File.Delete(dupPath);
}
catch { }

Console.WriteLine();
Console.WriteLine("========== 模块四：配置管理 ==========");
var (cfgPass, cfgFail) = SelfTest.ConfigTests.Run();
pass += cfgPass;
fail += cfgFail;

Console.WriteLine();
Console.WriteLine("========== 发布到 GitHub Pages ==========");
var (ghPass, ghFail) = SelfTest.GitHubTests.Run();
pass += ghPass;
fail += ghFail;

Console.WriteLine();
Console.WriteLine($"===== 合计：通过 {pass} 项，失败 {fail} 项 =====");
return fail == 0 ? 0 : 1;
