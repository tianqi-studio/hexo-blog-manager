using System.IO;

namespace HexoBlogManager.Services;

/// <summary>
/// 配置文件管理：定位、读取、备份、保存。
///
/// 涉及两个文件：
/// 1) Hexo 主配置 &lt;blog&gt;/_config.yml —— 网站标题、副标题、作者等
/// 2) Butterfly 主题配置 —— 导航菜单、页脚等
///
/// 主题配置的查找顺序（前面的优先）：
///   &lt;blog&gt;/_config.butterfly.yml     ← 推荐用户用这个，升级主题不会丢
///   &lt;blog&gt;/themes/butterfly/_config.yml
///   &lt;blog&gt;/node_modules/hexo-theme-butterfly/_config.yml
/// </summary>
public sealed class ConfigService
{
    private readonly string _blogRoot;

    public ConfigService(string blogRoot)
    {
        _blogRoot = blogRoot;
    }

    public string MainConfigPath => Path.Combine(_blogRoot, "_config.yml");

    public bool MainConfigExists => File.Exists(MainConfigPath);

    /// <summary>找到 Butterfly 主题配置文件；找不到返回 null。</summary>
    public string? FindThemeConfig()
    {
        string[] candidates =
        {
            Path.Combine(_blogRoot, "_config.butterfly.yml"),
            Path.Combine(_blogRoot, "_config.butterfly.yaml"),
            Path.Combine(_blogRoot, "themes", "butterfly", "_config.yml"),
            Path.Combine(_blogRoot, "node_modules", "hexo-theme-butterfly", "_config.yml")
        };

        foreach (string path in candidates)
        {
            try
            {
                if (File.Exists(path))
                    return path;
            }
            catch
            {
                // 路径不可访问，跳过
            }
        }

        return null;
    }

    /// <summary>用中文说明这个主题配置文件属于哪一类，提醒用户修改风险。</summary>
    public string DescribeThemeConfig(string path)
    {
        string normalized = path.Replace('/', '\\');

        if (normalized.Contains(@"\node_modules\"))
            return "正在编辑 npm 包内的主题默认配置。升级主题（npm update）会覆盖这里的修改，建议复制为根目录 _config.butterfly.yml。";

        if (normalized.Contains(@"\themes\butterfly\"))
            return "正在编辑主题目录内的配置。用 git 更新主题时可能被覆盖，建议复制为根目录 _config.butterfly.yml。";

        return "正在编辑根目录的独立主题配置，这是最推荐的位置，升级主题不会丢。";
    }

    public string Read(string path) => BlogSiteService.ReadTextUtf8(path);

    /// <summary>
    /// 备份配置文件，返回备份文件完整路径。
    /// 命名形如：_config.yml.20261120-204501.bak
    /// </summary>
    public string Backup(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("要备份的配置文件不存在。", filePath);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string backupPath = $"{filePath}.{stamp}.bak";

        File.Copy(filePath, backupPath, overwrite: true);
        return backupPath;
    }

    /// <summary>写回配置（UTF-8 无 BOM）。</summary>
    public void Save(string filePath, string content) => PostService.WriteText(filePath, content);

    /// <summary>只保留最近的若干个备份，避免备份目录越攒越多。</summary>
    public void CleanOldBackups(string filePath, int keep = 10)
    {
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;

            string pattern = Path.GetFileName(filePath) + ".*.bak";

            var old = Directory.GetFiles(dir, pattern)
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .Skip(keep)
                .ToList();

            foreach (string file in old)
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }
}
