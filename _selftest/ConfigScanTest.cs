using System.IO;
using System.Text;
using HexoBlogManager.Services;

namespace SelfTest;

/// <summary>
/// 配置自动识别模块的自检：字段映射、文件发现、优先级合并、以及各种「坏配置」场景。
/// 全部在临时目录里造真实文件跑，不依赖用户的博客。
/// </summary>
internal static class ConfigScanTests
{
    private const string Root = @"D:\selftest-config";

    /// <summary>带默认参数的断言委托：调用时可以只传两项。</summary>
    private delegate void CheckFn(string name, bool ok, string? detail = null);

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

        try
        {
            if (Directory.Exists(Root))
                ForceDelete(Root);

            Directory.CreateDirectory(Root);

            CheckFullConfig(Check);
            CheckMissingFields(Check);
            CheckBrokenFormats(Check);
            CheckAlternateConfig(Check);
            CheckThemeMerge(Check);
            CheckEncoding(Check);
            CheckMisc(Check);
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("[FAIL] 配置识别自检异常：" + ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(Root))
                    ForceDelete(Root);
            }
            catch { }
        }

        return (pass, fail);
    }

    // ==================================================================
    // 场景
    // ==================================================================

    private static void CheckFullConfig(CheckFn check)
    {
        string dir = NewCase("full");

        Write(dir, "_config.yml",
            """
            title: 我的博客
            subtitle: 记录与折腾
            description: 用来测试的站点
            author: 天奇
            language: zh-CN
            keywords: Hexo, 博客
            url: https://example.com
            theme: butterfly
            """);

        var snapshot = BlogConfigScanner.Scan(dir);

        check("完整配置：扫描无错误", snapshot.Error is null, snapshot.Error);
        check("完整配置：主配置存在", snapshot.MainExists);
        check("完整配置：主题名识别", snapshot.ThemeName == "butterfly", snapshot.ThemeName);

        checkField(check, snapshot, "title", "我的博客");
        checkField(check, snapshot, "subtitle", "记录与折腾");
        checkField(check, snapshot, "author", "天奇");
        checkField(check, snapshot, "language", "zh-CN");
        checkField(check, snapshot, "url", "https://example.com");

        var title = snapshot.ResolveMain("title");
        check("完整配置：title 状态为 Found", title.State == ConfigFieldState.Found, title.State.ToString());
        check("完整配置：title 带行号", title.SourceLine == 1, title.SourceLine.ToString());
        check("完整配置：title 来源文件正确",
            title.SourceFile.EndsWith("_config.yml", StringComparison.OrdinalIgnoreCase), title.SourceFile);
        check("完整配置：主配置是写入目标", snapshot.MainWritePath is not null);
    }

    private static void CheckMissingFields(CheckFn check)
    {
        string dir = NewCase("missing");

        Write(dir, "_config.yml",
            """
            title: 只有标题
            theme: butterfly
            """);

        var snapshot = BlogConfigScanner.Scan(dir);

        var subtitle = snapshot.ResolveMain("subtitle");
        check("字段缺失：状态为 Missing", subtitle.State == ConfigFieldState.Missing, subtitle.State.ToString());
        check("字段缺失：HasValue 为 false", !subtitle.HasValue);
        check("字段缺失：不影响其他字段", snapshot.ResolveMain("title").Text == "只有标题");
        check("字段缺失：不产生警告", snapshot.Files.All(f => f.Error is null));
    }

    private static void CheckBrokenFormats(CheckFn check)
    {
        // ---------- Tab 缩进 ----------
        string dirTab = NewCase("tab");
        Write(dirTab, "_config.yml", "title: 测试\nnav:\n\tdisplay_title: false\ntheme: butterfly\n");

        var snapTab = BlogConfigScanner.Scan(dirTab);
        check("Tab 缩进：仍能读到子键",
            snapTab.ResolveMain("nav.display_title").Text == "false",
            snapTab.ResolveMain("nav.display_title").Text);
        check("Tab 缩进：给出提示",
            snapTab.Files.Any(f => f.Warnings.Any(w => w.Contains("Tab"))));

        // ---------- 缺冒号 ----------
        string dirBad = NewCase("badline");
        Write(dirBad, "_config.yml",
            "title: 正常标题\n这一行没有冒号\nauthor: 正常作者\ntheme: butterfly\n");

        var snapBad = BlogConfigScanner.Scan(dirBad);
        check("坏行：标题仍可读", snapBad.ResolveMain("title").Text == "正常标题");
        check("坏行：作者仍可读", snapBad.ResolveMain("author").Text == "正常作者");
        check("坏行：给出提示",
            snapBad.Files.Any(f => f.Warnings.Any(w => w.Contains("缺少"))),
            string.Join("|", snapBad.Files.SelectMany(f => f.Warnings)));

        // ---------- 重复键 ----------
        string dirDup = NewCase("dup");
        Write(dirDup, "_config.yml", "title: 第一个\ntitle: 第二个\ntheme: butterfly\n");

        var snapDup = BlogConfigScanner.Scan(dirDup);
        check("重复键：按后出现的值显示", snapDup.ResolveMain("title").Text == "第二个",
            snapDup.ResolveMain("title").Text);
        check("重复键：给出提示",
            snapDup.Files.Any(f => f.Warnings.Any(w => w.Contains("重复"))));

        // ---------- 引号未闭合 ----------
        string dirQuote = NewCase("quote");
        Write(dirQuote, "_config.yml", "title: \"没闭合的引号\ntheme: butterfly\n");

        var snapQuote = BlogConfigScanner.Scan(dirQuote);
        check("引号未闭合：不抛异常", snapQuote.Error is null);
        check("引号未闭合：给出提示",
            snapQuote.Files.Any(f => f.Warnings.Any(w => w.Contains("引号"))));

        // ---------- 只有注释 / 空文件 ----------
        string dirEmpty = NewCase("empty");
        Write(dirEmpty, "_config.yml", "# 只有注释\n\n");

        var snapEmpty = BlogConfigScanner.Scan(dirEmpty);
        check("空配置：不抛异常", snapEmpty.Error is null);
        check("空配置：字段全部缺失", snapEmpty.ResolveMain("title").State == ConfigFieldState.Missing);
    }

    private static void CheckAlternateConfig(CheckFn check)
    {
        string dir = NewCase("alternate");

        Write(dir, "_config.yml",
            """
            title: 基础标题
            author: 基础作者
            theme: butterfly
            """);

        Write(dir, "_config.production.yml",
            """
            title: 线上标题
            """);

        var snapshot = BlogConfigScanner.Scan(dir);

        var title = snapshot.ResolveMain("title");
        check("覆盖配置：取到覆盖后的值", title.Text == "线上标题", title.Text);
        check("覆盖配置：状态标记为 Overridden", title.State == ConfigFieldState.Overridden, title.State.ToString());
        check("覆盖配置：提示写明会被覆盖",
            title.Note.Contains("覆盖") && title.Note.Contains("_config.yml"), title.Note);

        check("覆盖配置：未被覆盖的字段仍是 Found",
            snapshot.ResolveMain("author").State == ConfigFieldState.Found);

        check("覆盖配置：被识别为 Alternate 角色",
            snapshot.Files.Any(f => f.Role == ConfigRole.Alternate && f.FileName == "_config.production.yml"));
    }

    private static void CheckThemeMerge(CheckFn check)
    {
        string dir = NewCase("theme");

        Write(dir, "_config.yml", "title: 主题测试\ntheme: butterfly\n");

        // 根目录覆盖配置：只写了两项
        Write(dir, "_config.butterfly.yml",
            """
            nav:
              fixed: true
            menu:
              主页: / || fas fa-home
              归档: /archives/ || fas fa-archive
            """);

        // 主题自带默认配置：写了更多默认值
        WriteDir(dir, "node_modules/hexo-theme-butterfly");
        Write(dir, "node_modules/hexo-theme-butterfly/_config.yml",
            """
            nav:
              display_title: false
              fixed: false
            footer:
              custom_text: 主题默认页脚
              copyright:
                enable: false
            """);

        var snapshot = BlogConfigScanner.Scan(dir);

        check("主题合并：写入目标是根目录覆盖配置",
            snapshot.ThemeWritePath is not null &&
            snapshot.ThemeWritePath.EndsWith("_config.butterfly.yml", StringComparison.OrdinalIgnoreCase),
            snapshot.ThemeWritePath);

        var fixedValue = snapshot.ResolveTheme("nav.fixed");
        check("主题合并：覆盖层的值优先", fixedValue.Text == "true" && fixedValue.State == ConfigFieldState.Found,
            $"{fixedValue.Text}/{fixedValue.State}");

        var displayTitle = snapshot.ResolveTheme("nav.display_title");
        check("主题合并：缺失字段继承主题默认",
            displayTitle.Text == "false" && displayTitle.State == ConfigFieldState.Inherited,
            $"{displayTitle.Text}/{displayTitle.State}");
        check("主题合并：继承提示写明来源", displayTitle.Note.Contains("继承"), displayTitle.Note);

        var footer = snapshot.ResolveTheme("footer.custom_text");
        check("主题合并：深层字段也能继承", footer.Text == "主题默认页脚", footer.Text);

        var menu = snapshot.GetMenu();
        check("主题合并：菜单只取写入目标", menu.Count == 2, menu.Count.ToString());
        check("主题合并：菜单键值正确",
            menu.Any(m => m.Key == "主页" && m.Value == "/ || fas fa-home"),
            string.Join("|", menu.Select(m => m.Key + "=" + m.Value)));

        check("主题合并：_config.butterfly.yml 不被当成环境覆盖配置",
            snapshot.Files.All(f => f.Role != ConfigRole.Alternate),
            string.Join("|", snapshot.Files.Where(f => f.Role == ConfigRole.Alternate).Select(f => f.FileName)));

        // ---------- 去掉根目录覆盖层，写入目标应回落到主题目录 ----------

        string dir2 = NewCase("theme2");
        Write(dir2, "_config.yml", "title: 回落测试\ntheme: butterfly\n");
        WriteDir(dir2, "themes/butterfly");
        Write(dir2, "themes/butterfly/_config.yml", "nav:\n  fixed: true\n");

        var snap2 = BlogConfigScanner.Scan(dir2);
        check("主题合并：没有根目录覆盖时回落到 themes 目录",
            snap2.ThemeWritePath is not null &&
            snap2.ThemeWritePath.Replace('/', '\\').Contains(@"themes\butterfly\", StringComparison.OrdinalIgnoreCase),
            snap2.ThemeWritePath);
        check("主题合并：回落时字段为 Found",
            snap2.ResolveTheme("nav.fixed").State == ConfigFieldState.Found,
            snap2.ResolveTheme("nav.fixed").State.ToString());
    }

    private static void CheckEncoding(CheckFn check)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // ---------- GBK ----------
        string dir = NewCase("gbk");
        string content = "title: 中文标题测试\nsubtitle: 副标题\ntheme: butterfly\n";

        byte[] gbkBytes = Encoding.GetEncoding("GB18030").GetBytes(content);
        File.WriteAllBytes(Path.Combine(dir, "_config.yml"), gbkBytes);

        var snapshot = BlogConfigScanner.Scan(dir);

        check("GBK 编码：中文正确解码", snapshot.ResolveMain("title").Text == "中文标题测试",
            snapshot.ResolveMain("title").Text);
        check("GBK 编码：给出编码说明",
            snapshot.Files.Any(f => f.EncodingNote is not null && f.EncodingNote.Contains("GB18030")),
            snapshot.Files.FirstOrDefault()?.EncodingNote);

        // ---------- UTF-8 BOM ----------
        string dirBom = NewCase("bom");
        var bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(Path.Combine(dirBom, "_config.yml"),
            "title: BOM 标题\ntheme: butterfly\n", bom);

        var snapBom = BlogConfigScanner.Scan(dirBom);
        check("UTF-8 BOM：首行字段不被 BOM 干扰",
            snapBom.ResolveMain("title").Text == "BOM 标题", snapBom.ResolveMain("title").Text);
    }

    private static void CheckMisc(CheckFn check)
    {
        // ---------- 多行文本块 ----------
        string dirBlock = NewCase("block");
        Write(dirBlock, "_config.yml",
            "title: 多行测试\ndescription: |\n  第一行说明\n  第二行说明\ntheme: butterfly\n");

        var snapBlock = BlogConfigScanner.Scan(dirBlock);
        string desc = snapBlock.ResolveMain("description").Text;
        check("多行文本块：内容完整", desc.Contains("第一行说明") && desc.Contains("第二行说明"), desc);

        // ---------- 行内列表 ----------
        string dirInline = NewCase("inline");
        Write(dirInline, "_config.yml", "keywords: [Hexo, \"博客\", 建站]\ntheme: butterfly\n");

        var snapInline = BlogConfigScanner.Scan(dirInline);
        check("行内列表：展开成逗号分隔",
            snapInline.ResolveMain("keywords").Text == "Hexo, 博客, 建站",
            snapInline.ResolveMain("keywords").Text);

        // ---------- 列表项 ----------
        string dirList = NewCase("list");
        Write(dirList, "_config.yml", "keywords:\n  - Hexo\n  - 博客\ntheme: butterfly\n");

        var snapList = BlogConfigScanner.Scan(dirList);
        check("列表项：拼接成逗号分隔",
            snapList.ResolveMain("keywords").Text == "Hexo, 博客",
            snapList.ResolveMain("keywords").Text);

        // ---------- 目录不存在 ----------
        var missing = BlogConfigScanner.Scan(@"D:\definitely-not-exist-selftest");
        check("目录不存在：返回错误而不是抛异常",
            missing.Error is not null && missing.Error.Contains("不存在"), missing.Error);

        // ---------- 主配置完全没有 ----------
        string dirNoMain = NewCase("nomain");
        WriteDir(dirNoMain, "source/_posts");

        var snapNoMain = BlogConfigScanner.Scan(dirNoMain);
        check("没有 _config.yml：不抛异常", snapNoMain.Error is null);
        check("没有 _config.yml：MainExists 为 false", !snapNoMain.MainExists);
        check("没有 _config.yml：给出提示",
            snapNoMain.Warnings.Any(w => w.Contains("_config.yml")),
            string.Join("|", snapNoMain.Warnings));

        // ---------- 没有 theme 字段 ----------
        string dirNoTheme = NewCase("notheme");
        Write(dirNoTheme, "_config.yml", "title: 没有主题字段\n");

        var snapNoTheme = BlogConfigScanner.Scan(dirNoTheme);
        check("缺少 theme 字段：给出提示",
            snapNoTheme.Warnings.Any(w => w.Contains("theme")),
            string.Join("|", snapNoTheme.Warnings));

        // ---------- 只读不写 ----------
        string dirReadOnly = NewCase("readonly");
        string configPath = Write(dirReadOnly, "_config.yml", "title: 只读检查\ntheme: butterfly\n");
        string before = File.ReadAllText(configPath);
        DateTime beforeTime = File.GetLastWriteTimeUtc(configPath);

        BlogConfigScanner.Scan(dirReadOnly);

        check("识别过程不修改文件内容", File.ReadAllText(configPath) == before);
        check("识别过程不修改文件时间", File.GetLastWriteTimeUtc(configPath) == beforeTime);

        // ---------- 写入：布尔值不能被加引号 ----------
        const string navContent = "nav:\n  fixed: false\n";

        string rawWrite = YamlPathEditor.SetScalar(navContent, "nav.fixed", "true", raw: true);
        check("布尔写入不加引号", rawWrite.Contains("fixed: true"), rawWrite);

        string escapedWrite = YamlPathEditor.SetScalar(navContent, "nav.fixed", "true");
        check("普通写入仍会加引号（保护标题这类值）",
            escapedWrite.Contains("fixed: \"true\""), escapedWrite);

        string sameWrite = YamlPathEditor.SetScalar(navContent, "nav.fixed", "false", raw: true);
        check("写入相同值时内容不变", sameWrite == navContent, sameWrite.Replace("\n", "\\n"));
    }

    // ==================================================================
    // 工具
    // ==================================================================

    private static void checkField(
        CheckFn check, BlogConfigSnapshot snapshot, string path, string expected)
    {
        var value = snapshot.ResolveMain(path);
        check($"完整配置：{path} 解析正确", value.Text == expected, value.Text);
    }

    private static string NewCase(string name)
    {
        string dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Write(string dir, string relativePath, string content)
    {
        string path = Path.Combine(dir, relativePath);
        string? parent = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        File.WriteAllText(path, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
        return path;
    }

    private static void WriteDir(string dir, string relativePath)
        => Directory.CreateDirectory(Path.Combine(dir, relativePath));

    private static void ForceDelete(string dir)
    {
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
}
