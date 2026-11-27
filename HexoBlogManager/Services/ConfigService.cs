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
    /// 同一秒内连存两次会顺延成 ...-204501-1.bak，不覆盖已有备份。
    /// </summary>
    public string Backup(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("要备份的配置文件不存在。", filePath);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string backupPath = $"{filePath}.{stamp}.bak";

        int suffix = 1;
        while (File.Exists(backupPath))
        {
            backupPath = $"{filePath}.{stamp}-{suffix}.bak";
            suffix++;
        }

        File.Copy(filePath, backupPath, overwrite: false);
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

    /// <summary>
    /// 列出某个配置文件的全部备份，最新的在前。
    /// 备份和配置文件在同一目录，命名形如 _config.yml.20261120-204501.bak。
    /// </summary>
    public List<ConfigBackupEntry> ListBackups(string filePath)
    {
        var result = new List<ConfigBackupEntry>();

        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return result;

            string prefix = Path.GetFileName(filePath) + ".";

            foreach (string file in Directory.EnumerateFiles(dir, prefix + "*.bak"))
            {
                try
                {
                    var info = new FileInfo(file);
                    result.Add(new ConfigBackupEntry(file, ParseBackupTime(file, prefix, info.LastWriteTime), info.Length));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("读取配置备份列表失败：" + ex.Message);
        }

        return result.OrderByDescending(e => e.Time).ToList();
    }

    /// <summary>
    /// 用某个备份还原配置文件。
    /// 还原前会先把当前文件也备份一次，万一选中了更旧的版本还能退回来。
    /// </summary>
    public bool RestoreBackup(string backupPath, string targetPath, out string error)
    {
        error = string.Empty;

        try
        {
            if (!File.Exists(backupPath))
            {
                error = "备份文件已经不存在了。";
                return false;
            }

            if (File.Exists(targetPath))
                Backup(targetPath);

            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.Copy(backupPath, targetPath, overwrite: true);
            CleanOldBackups(targetPath);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>从 _config.yml.20261120-204501.bak 这种文件名里取时间。</summary>
    private static DateTime ParseBackupTime(string path, string prefix, DateTime fallback)
    {
        string name = Path.GetFileName(path);

        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            return fallback;

        string stamp = name[prefix.Length..^4];

        // 同一秒内的第二份备份会带 -1 / -2 后缀，解析时间时要去掉
        int extra = stamp.IndexOf('-', stamp.IndexOf('-') + 1);
        if (extra > 0)
            stamp = stamp[..extra];

        return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var time)
            ? time
            : fallback;
    }
}

/// <summary>一条配置备份记录。</summary>
/// <param name="FilePath">备份文件路径。</param>
/// <param name="Time">备份时间（取自文件名）。</param>
/// <param name="Size">字节数。</param>
public sealed record ConfigBackupEntry(string FilePath, DateTime Time, long Size)
{
    public string TimeDisplay => Time.ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeDisplay => Size < 1024 ? $"{Size} B" : $"{Size / 1024.0:F1} KB";
}
