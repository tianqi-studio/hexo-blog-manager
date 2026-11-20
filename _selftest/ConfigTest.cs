using System.IO;
using HexoBlogManager.Services;

namespace SelfTest;

/// <summary>模块四自检：YAML 按路径读写、保真、备份、配置改动后 Hexo 是否仍能生成。</summary>
internal static class ConfigTests
{
    private const string BlogRoot = @"D:\testblog";

    public static (int Pass, int Fail) Run()
    {
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
                Console.WriteLine($"[FAIL] {name}" + (detail is null ? "" : $"  → {detail}"));
            }
        }

        string mainPath = Path.Combine(BlogRoot, "_config.yml");
        string themePath = Path.Combine(BlogRoot, "_config.butterfly.yml");

        string main = File.ReadAllText(mainPath);
        string theme = File.ReadAllText(themePath);

        // ---------- 顶层标量读取 ----------
        Check("读取 title", YamlPathEditor.GetScalar(main, "title") == "Hexo",
            YamlPathEditor.GetScalar(main, "title"));
        Check("读取 language", YamlPathEditor.GetScalar(main, "language") == "en",
            YamlPathEditor.GetScalar(main, "language"));
        Check("读取 theme", YamlPathEditor.GetScalar(main, "theme") == "butterfly",
            YamlPathEditor.GetScalar(main, "theme"));
        Check("读取 author", YamlPathEditor.GetScalar(main, "author") == "John Doe",
            YamlPathEditor.GetScalar(main, "author"));

        // ---------- 顶层标量写入：只改一行 ----------
        string mainModified = YamlPathEditor.SetScalar(main, "title", "我的测试博客");

        Check("标题已修改", YamlPathEditor.GetScalar(mainModified, "title") == "我的测试博客",
            YamlPathEditor.GetScalar(mainModified, "title"));
        Check("总行数不变", mainModified.Split('\n').Length == main.Split('\n').Length,
            $"{mainModified.Split('\n').Length} vs {main.Split('\n').Length}");
        Check("author 未被改动",
            YamlPathEditor.GetScalar(mainModified, "author") == YamlPathEditor.GetScalar(main, "author"));
        Check("注释被保留", mainModified.Contains("# Docs: https://hexo.io/docs/configuration"));
        Check("deploy 段被保留", mainModified.Contains("deploy:")
                                 && mainModified.Contains("type: ''"));

        // ---------- 中文与特殊字符转义 ----------
        string quoted = YamlPathEditor.SetScalar(main, "subtitle", "带: 冒号的副标题");
        string subtitleLine = quoted.Split('\n').First(l => l.StartsWith("subtitle"));
        Check("含冒号的值被加引号", subtitleLine.Contains('"'), subtitleLine.Trim());
        Check("加引号后仍能读回", YamlPathEditor.GetScalar(quoted, "subtitle") == "带: 冒号的副标题",
            YamlPathEditor.GetScalar(quoted, "subtitle"));

        // ---------- 主题配置：映射读取 ----------
        var menu = YamlPathEditor.GetMapping(theme, "menu");
        Check("菜单解析出 4 项", menu.Count == 4, menu.Count.ToString());
        Check("菜单第一项正确", menu.Count > 0 && menu[0].Key == "主页",
            menu.Count > 0 ? menu[0].Key : "空");
        Check("菜单值含图标", menu.Count > 0 && menu[0].Value.Contains("fas fa-home"),
            menu.Count > 0 ? menu[0].Value : "空");

        // ---------- 主题配置：嵌套标量 ----------
        Check("nav 路径存在", YamlPathEditor.Exists(theme, "nav"));
        Check("nav.display_title 可读", YamlPathEditor.Exists(theme, "nav.display_title"));

        bool displayTitle = YamlPathEditor.GetBool(theme, "nav.display_title", false);
        string themeModified = YamlPathEditor.SetScalar(theme, "nav.display_title", displayTitle ? "false" : "true");

        Check("嵌套布尔已反转",
            YamlPathEditor.GetBool(themeModified, "nav.display_title", false) != displayTitle);
        Check("主题其他段未被破坏",
            YamlPathEditor.GetScalar(themeModified, "index_img") == YamlPathEditor.GetScalar(theme, "index_img"));

        // ---------- 页脚嵌套写入 ----------
        string themeFooter = YamlPathEditor.SetScalar(theme, "footer.custom_text", "本站由 Hexo 驱动");
        Check("页脚文字已写入",
            YamlPathEditor.GetScalar(themeFooter, "footer.custom_text") == "本站由 Hexo 驱动",
            YamlPathEditor.GetScalar(themeFooter, "footer.custom_text"));
        Check("页脚布尔项仍然可读", YamlPathEditor.Exists(themeFooter, "footer.copyright.enable"));

        // ---------- 映射整体写入 ----------
        var menuPlus = YamlPathEditor.GetMapping(themeFooter, "menu");
        menuPlus.Add(("关于", "/about/ || fas fa-user"));

        string themeMenu = YamlPathEditor.SetMapping(themeFooter, "menu", menuPlus);

        Check("菜单新增成功", YamlPathEditor.GetMapping(themeMenu, "menu").Count == 5,
            YamlPathEditor.GetMapping(themeMenu, "menu").Count.ToString());
        Check("菜单写入不影响页脚",
            YamlPathEditor.GetScalar(themeMenu, "footer.custom_text") == "本站由 Hexo 驱动");
        Check("菜单写入不影响 nav", YamlPathEditor.Exists(themeMenu, "nav.display_title"));

        // ---------- 缺失配置段的自动创建（giscus 是最典型场景） ----------
        string minimal = "root: test\ncomments:\n  use:\n";

        string g1 = YamlPathEditor.SetScalar(minimal, "comments.use", "Giscus");
        Check("comments.use 写入成功", YamlPathEditor.GetScalar(g1, "comments.use") == "Giscus",
            YamlPathEditor.GetScalar(g1, "comments.use"));

        string g2 = YamlPathEditor.SetScalar(minimal, "giscus.repo", "owner/repo");
        Check("缺失的 giscus 段被自动创建",
            YamlPathEditor.GetScalar(g2, "giscus.repo") == "owner/repo", g2);
        Check("自动创建时不破坏原有内容", g2.Contains("root: test") && g2.Contains("comments:"), g2);

        string g3 = YamlPathEditor.SetScalar(g2, "giscus.repo_id", "R_abc123");
        Check("同级子键插入到正确位置",
            YamlPathEditor.GetScalar(g3, "giscus.repo_id") == "R_abc123", g3);

        string g4 = YamlPathEditor.SetScalar(g3, "giscus.category_id", "DIC_xyz");
        Check("第三个子键写入正确",
            YamlPathEditor.GetScalar(g4, "giscus.category_id") == "DIC_xyz", g4);

        Check("giscus 段结构完整（三个键同缩进）",
            g4.Contains("giscus:\n  repo: owner/repo\n  repo_id: R_abc123\n  category_id: DIC_xyz"), g4);

        // 清空某个值时应写成空值而不是带引号的空串
        string g5 = YamlPathEditor.SetScalar(g4, "giscus.repo", "");
        Check("清空后写成空值", YamlPathEditor.GetScalar(g5, "giscus.repo") == string.Empty, g5);

        // ---------- 备份 ----------
        var configService = new ConfigService(BlogRoot);

        string tempPath = Path.Combine(BlogRoot, "selftest-config.yml");
        File.WriteAllText(tempPath, main);

        string backupPath = configService.Backup(tempPath);
        Check("备份文件已生成", File.Exists(backupPath), backupPath);
        Check("备份内容与原文件一致", File.ReadAllText(backupPath) == main);
        Check("备份文件名带 .bak 后缀", Path.GetFileName(backupPath).EndsWith(".bak"),
            Path.GetFileName(backupPath));

        // ---------- 端到端：真的改一次配置，确认 Hexo 仍能生成 ----------
        string originalMain = File.ReadAllText(mainPath);
        string originalTheme = File.ReadAllText(themePath);

        try
        {
            string e2e = YamlPathEditor.SetScalar(originalMain, "title", "测试站");
            e2e = YamlPathEditor.SetScalar(e2e, "subtitle", "端到端验证: 带冒号的副标题");
            File.WriteAllText(mainPath, e2e);

            string themeE2E = YamlPathEditor.SetMapping(originalTheme, "menu", menuPlus);
            File.WriteAllText(themePath, themeE2E);

            var result = ProcessRunner.RunAsync("hexo generate", BlogRoot, TimeSpan.FromSeconds(120))
                                      .GetAwaiter().GetResult();

            Check("改配置后 hexo generate 成功", result.Success,
                result.CombinedOutput.Length > 400
                    ? result.CombinedOutput[..400]
                    : result.CombinedOutput);
        }
        finally
        {
            File.WriteAllText(mainPath, originalMain);
            File.WriteAllText(themePath, originalTheme);
        }

        Check("主配置已恢复", File.ReadAllText(mainPath) == originalMain);
        Check("主题配置已恢复", File.ReadAllText(themePath) == originalTheme);

        // ---------- 清理 ----------
        try
        {
            File.Delete(backupPath);
            File.Delete(tempPath);
        }
        catch { }

        return (pass, fail);
    }
}
