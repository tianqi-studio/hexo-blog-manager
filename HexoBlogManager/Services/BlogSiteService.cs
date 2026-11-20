using System.IO;
using System.Text;
using HexoBlogManager.Models;

namespace HexoBlogManager.Services;

/// <summary>
/// 博客目录校验与信息读取。
///
/// 校验规则（按需求）：
/// 1) 目录必须存在
/// 2) 根目录必须有 _config.yml
/// 3) 必须有 source/_posts 文件夹
/// 任何一条不满足都返回失败，并给出「可能是什么原因、该怎么处理」的中文提示。
///
/// 兼容性处理：
/// · Path.GetFullPath 会处理 . / .. / 相对路径 / 尾部斜杠
/// · 中文路径、带空格路径在 .NET 里是原生 Unicode，无需特殊转义
/// · 用户从资源管理器「复制路径」得到的是带引号的字符串，这里会先去掉首尾引号
/// · 长路径（>260 字符）在 .NET Core 及以上默认已支持
/// </summary>
public sealed class BlogSiteService
{
    /// <summary>校验一个目录是否是合法的 Hexo 博客根目录。</summary>
    public ValidationResult Validate(string? path)
    {
        // ---------- 1. 参数与路径合法性 ----------
        if (string.IsNullOrWhiteSpace(path))
        {
            return ValidationResult.Fail(
                "尚未选择博客目录",
                "点击「选择博客文件夹」，选中你的 Hexo 博客根目录（包含 _config.yml 和 source 的那一层）。");
        }

        // 用户可能直接从资源管理器复制了带引号的路径
        string cleaned = path.Trim().Trim('"', '\'');

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(cleaned);
        }
        catch (Exception ex)
        {
            return ValidationResult.Fail(
                "路径格式不正确",
                $"无法解析这个路径：{cleaned}\n原因：{ex.Message}\n\n请重新选择一次目录。");
        }

        if (!Directory.Exists(fullPath))
        {
            return ValidationResult.Fail(
                "目录不存在",
                $"路径：{fullPath}\n\n可能的原因：\n" +
                "· 目录被移动、重命名或删除了\n" +
                "· 博客放在移动硬盘 / U 盘上，设备没有插好\n" +
                "· 博客放在网络位置，当前无法访问\n\n" +
                "建议：点击「选择博客文件夹」重新指定。");
        }

        // ---------- 2. 关键文件 / 目录检查 ----------
        string configPath = Path.Combine(fullPath, "_config.yml");
        string postsPath = Path.Combine(fullPath, "source", "_posts");

        bool hasConfig = File.Exists(configPath);
        bool hasPosts = Directory.Exists(postsPath);

        // 智能识别「选错层级」：这是新手最常见的错误。
        // 如果当前目录没有 _config.yml，但它的上一级有，说明用户选到了 source / themes 之类的子目录。
        if (!hasConfig)
        {
            try
            {
                string? parent = Directory.GetParent(fullPath)?.FullName;
                if (parent is not null && File.Exists(Path.Combine(parent, "_config.yml")))
                {
                    return ValidationResult.Fail(
                        "这是博客的子目录，不是根目录",
                        $"你选的是：{fullPath}\n" +
                        $"正确的根目录应该是：{parent}\n\n" +
                        "请点击「选择博客文件夹」，选中上面这个上级目录。");
                }
            }
            catch
            {
                // 取父目录失败（权限等），继续走下面的通用判断
            }
        }

        if (!hasConfig && !hasPosts)
        {
            return ValidationResult.Fail(
                "这不是一个 Hexo 博客根目录",
                "该目录下既没有 _config.yml，也没有 source/_posts 文件夹。\n\n" +
                "请检查是否选错了层级：\n" +
                "· 正确：D:\\blog（里面有 _config.yml、source、themes）\n" +
                "· 错误：D:\\blog\\source（这是子目录）\n\n" +
                "如果还没建博客，可以先在命令行执行：hexo init D:\\blog");
        }

        if (!hasConfig)
        {
            return ValidationResult.Fail(
                "缺少 _config.yml 配置文件",
                $"找到了 source/_posts，但根目录下没有 _config.yml。\n\n" +
                "最常见的原因是选到了 source 目录。\n" +
                "建议：点「选择博客文件夹」后，选中 source 的**上一级**目录。");
        }

        if (!hasPosts)
        {
            return ValidationResult.Fail(
                "缺少 source/_posts 文件夹",
                "_config.yml 找到了，但 source/_posts 不存在。\n\n" +
                "可能的原因：\n" +
                "· 目录被误删\n" +
                "· 博客尚未初始化完整\n\n" +
                "建议：在博客根目录手动创建 source\\_posts 文件夹后重试。");
        }

        // ---------- 3. 读取 _config.yml ----------
        string configText;
        try
        {
            configText = ReadTextUtf8(configPath);
        }
        catch (UnauthorizedAccessException)
        {
            return ValidationResult.Fail(
                "没有权限读取 _config.yml",
                "系统拒绝访问这个文件。\n\n" +
                "建议：\n" +
                "· 右键博客目录 → 属性 → 安全，确认当前用户有读取权限\n" +
                "· 如果文件在 C 盘系统目录下，考虑把博客移到用户目录（如 D:\\blog）");
        }
        catch (IOException ex)
        {
            return ValidationResult.Fail(
                "读取 _config.yml 失败",
                $"{ex.Message}\n\n可能该文件正被编辑器独占锁定，关闭编辑器后重试。");
        }

        var yaml = YamlMiniParser.ParseTopLevel(configText);
        string theme = YamlMiniParser.GetValue(yaml, "theme");

        // ---------- 4. 统计文章数、检查主题是否安装 ----------
        int postCount = CountMarkdownFiles(postsPath);
        bool themeInstalled = IsThemeInstalled(fullPath, theme);

        DateTime? configWrite = null;
        try { configWrite = File.GetLastWriteTime(configPath); } catch { }

        var site = new BlogSiteInfo
        {
            RootPath = fullPath,
            Title = YamlMiniParser.GetValue(yaml, "title"),
            Subtitle = YamlMiniParser.GetValue(yaml, "subtitle"),
            Author = YamlMiniParser.GetValue(yaml, "author"),
            Theme = theme,
            ThemeInstalled = themeInstalled,
            PostCount = postCount,
            ConfigPath = configPath,
            ConfigLastWrite = configWrite,
            PostsPath = postsPath
        };

        return ValidationResult.Ok(site);
    }

    /// <summary>
    /// 统计 source/_posts 下的 Markdown 文件数量（含子目录，因为 Hexo 支持把文章放进子目录）。
    /// 统计过程中任何访问异常都不抛出，只跳过。
    /// </summary>
    public static int CountMarkdownFiles(string postsPath)
    {
        if (!Directory.Exists(postsPath))
            return 0;

        int count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(postsPath, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
        }
        catch
        {
            // 权限不足或目录被占用：返回已统计到的数量，不影响校验结果
        }
        return count;
    }

    /// <summary>
    /// 判断主题是否已经安装。
    /// Hexo 的主题有两种安装方式，两种都要认：
    /// 1) 放在博客的 themes/主题名 目录（git clone 方式）
    /// 2) 作为 npm 包装在 node_modules/hexo-theme-主题名（npm install 方式）
    /// </summary>
    public static bool IsThemeInstalled(string blogRoot, string theme)
    {
        if (string.IsNullOrWhiteSpace(theme))
            return false;

        try
        {
            if (Directory.Exists(Path.Combine(blogRoot, "themes", theme)))
                return true;

            if (Directory.Exists(Path.Combine(blogRoot, "node_modules", "hexo-theme-" + theme)))
                return true;
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 以 UTF-8 读取文本文件。
    /// 使用宽松解码（非法字节替换为 �）而不是抛异常，保证「配置文件里有一两个坏字节」时
    /// 工具仍然可用，不会整个校验失败。
    /// </summary>
    public static string ReadTextUtf8(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);

        // 手动剥掉 UTF-8 BOM，否则第一个键名会带不可见字符，导致 title 读不到
        int offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            offset = 3;

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
            .GetString(bytes, offset, bytes.Length - offset);
    }
}
