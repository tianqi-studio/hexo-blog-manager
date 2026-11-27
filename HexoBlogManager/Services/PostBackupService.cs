using System.IO;
using System.Text;

namespace HexoBlogManager.Services;

/// <summary>一条文章历史记录。</summary>
/// <param name="FilePath">备份文件的完整路径。</param>
/// <param name="Time">备份时间（取自文件名，精确到毫秒）。</param>
/// <param name="Size">字节数。</param>
public sealed record PostBackupEntry(string FilePath, DateTime Time, long Size)
{
    /// <summary>显示到毫秒：同一秒内连续保存两次时，只有毫秒能区分先后。</summary>
    public string TimeDisplay => Time.ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string SizeDisplay => Size < 1024 ? $"{Size} B" : $"{Size / 1024.0:F1} KB";
}

/// <summary>
/// 文章历史备份。
///
/// 为什么需要：删除有回收站兜底，但「改坏了」救不回来。
/// 覆盖写之前留一份原文，误删一段、正则替换写错、保存时手滑全选删除，
/// 都能从这里退回去。
///
/// 存放位置：&lt;博客根目录&gt;/.hexo-manager/backups/posts/
/// 放博客目录而不是 %AppData%，是为了备份跟着博客走——换台机器也还在。
/// 目录里会写一个 .gitignore（内容为 *），避免污染博客自身的 git 状态。
/// </summary>
public sealed class PostBackupService
{
    /// <summary>每篇文章保留的历史份数。</summary>
    private const int KeepPerPost = 10;

    /// <summary>整个备份目录的总份数上限，防止长期使用把磁盘吃满。</summary>
    private const int KeepTotal = 300;

    private readonly string _blogRoot;

    public PostBackupService(string blogRoot)
    {
        _blogRoot = blogRoot;
    }

    /// <summary>备份根目录。</summary>
    public string BackupRoot => Path.Combine(_blogRoot, ".hexo-manager", "backups", "posts");

    /// <summary>
    /// 在覆盖写之前备份现有文件。
    /// 返回备份文件路径；文件不存在、或备份失败时返回 null（不阻断保存）。
    /// </summary>
    public string? BackupBeforeSave(string filePath, string? relativePath = null)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            Directory.CreateDirectory(BackupRoot);
            EnsureGitIgnore();

            string key = BuildKey(filePath, relativePath);
            string name = $"{key}.{DateTime.Now:yyyyMMdd-HHmmssfff}.bak";
            string target = Path.Combine(BackupRoot, name);

            // 同一毫秒重复保存：追加序号，不覆盖上一条
            int suffix = 1;
            while (File.Exists(target))
            {
                target = Path.Combine(BackupRoot, $"{key}.{DateTime.Now:yyyyMMdd-HHmmssfff}-{suffix}.bak");
                suffix++;
            }

            File.Copy(filePath, target, overwrite: false);

            TrimPerPost(key);
            TrimTotal();

            return target;
        }
        catch (Exception ex)
        {
            // 备份失败不能影响保存本身，记一条日志就够
            LogService.Warn($"备份文章失败（{Path.GetFileName(filePath)}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>列出某篇文章的历史，最新的在前。</summary>
    public List<PostBackupEntry> List(string filePath, string? relativePath = null)
    {
        var result = new List<PostBackupEntry>();

        try
        {
            if (!Directory.Exists(BackupRoot))
                return result;

            string key = BuildKey(filePath, relativePath);

            foreach (string file in Directory.EnumerateFiles(BackupRoot, key + ".*.bak"))
            {
                try
                {
                    var info = new FileInfo(file);
                    result.Add(new PostBackupEntry(file, ParseTime(file, info.LastWriteTime), info.Length));
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("读取文章历史失败：" + ex.Message);
        }

        return result.OrderByDescending(e => e.Time).ToList();
    }

    /// <summary>
    /// 用某个历史版本覆盖当前文件。
    /// 覆盖前会先把「当前内容」也备份一次，恢复错了还能再退回来。
    /// </summary>
    public bool Restore(string backupPath, string targetPath, string? relativePath, out string error)
    {
        error = string.Empty;

        try
        {
            if (!File.Exists(backupPath))
            {
                error = "备份文件已经不存在了。";
                return false;
            }

            // 先给当前版本留一份，避免「恢复到一个更差的版本」之后无路可退
            if (File.Exists(targetPath))
                BackupBeforeSave(targetPath, relativePath);

            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.Copy(backupPath, targetPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>在备份目录里放一个 .gitignore，让博客的 git 仓库忽略这些文件。</summary>
    private void EnsureGitIgnore()
    {
        try
        {
            string path = Path.Combine(_blogRoot, ".hexo-manager", ".gitignore");
            if (File.Exists(path))
                return;

            File.WriteAllText(path, "# 该目录由 Hexo 博客管理工具自动生成\n*\n",
                new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>把文章路径压成一个安全的文件名前缀。</summary>
    private string BuildKey(string filePath, string? relativePath)
    {
        string relative = relativePath ?? Path.GetFileName(filePath);

        // 去掉上层的 source/_posts 之类前缀，只保留文章之间的区分部分
        var chars = relative.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) || c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar
                ? '_'
                : c).ToArray();

        string key = new string(chars).Trim('_');
        return key.Length == 0 ? "post" : key;
    }

    /// <summary>从形如 name.20261127-200855123.bak 的文件名里取时间。</summary>
    private static DateTime ParseTime(string path, DateTime fallback)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
            return fallback;

        string stamp = name[(dot + 1)..];
        int dash = stamp.IndexOf('-');
        if (dash < 0)
            return fallback;

        // 去掉可能存在的 -1 这类重名后缀
        string cleaned = stamp;
        int tail = cleaned.IndexOf('-', dash + 1);
        if (tail > 0)
            cleaned = cleaned[..tail];

        return DateTime.TryParseExact(cleaned, "yyyyMMdd-HHmmssfff",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var time)
            ? time
            : fallback;
    }

    /// <summary>单篇文章只留最近 KeepPerPost 份。</summary>
    private void TrimPerPost(string key)
    {
        try
        {
            var files = Directory.EnumerateFiles(BackupRoot, key + ".*.bak")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)   // 文件名里的时间戳可直接排序
                .Skip(KeepPerPost)
                .ToList();

            foreach (var file in files)
            {
                try { file.Delete(); } catch { }
            }
        }
        catch { }
    }

    /// <summary>总量超限时按修改时间删最旧的。</summary>
    private void TrimTotal()
    {
        try
        {
            var files = Directory.EnumerateFiles(BackupRoot, "*.bak")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeepTotal)
                .ToList();

            foreach (var file in files)
            {
                try { file.Delete(); } catch { }
            }
        }
        catch { }
    }
}
