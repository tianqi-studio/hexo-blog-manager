using System.IO;
using System.Text;
using HexoBlogManager.Models;

namespace HexoBlogManager.Services;

/// <summary>
/// 文章读写服务：扫描、解析、保存、新建、删除、图片转存。
///
/// 所有方法都基于「博客根目录」构造，不依赖全局状态，方便测试与复用。
/// </summary>
public sealed class PostService
{
    private readonly string _blogRoot;

    public PostService(string blogRoot)
    {
        _blogRoot = blogRoot;
        Backups = new PostBackupService(blogRoot);
    }

    /// <summary>文章历史备份（编辑器里的「历史版本」直接用这个）。</summary>
    public PostBackupService Backups { get; }

    /// <summary>
    /// 上一次 LoadAll 里读取失败、被跳过的文件。
    /// 单个文件坏掉不该拖垮整个列表，但也不该让用户毫不知情——
    /// 列表页会用它提示「有 N 篇没读出来」。
    /// </summary>
    public IReadOnlyList<string> LastSkippedFiles { get; private set; } = Array.Empty<string>();

    /// <summary>source/_posts 的完整路径。</summary>
    public string PostsDirectory => Path.Combine(_blogRoot, "source", "_posts");

    /// <summary>文章资源目录 source/images。</summary>
    public string ImagesDirectory => Path.Combine(_blogRoot, "source", "images");

    // ==================================================================
    // 读取
    // ==================================================================

    /// <summary>
    /// 扫描全部文章。单个文件解析失败会被跳过，不影响其他文章。
    /// 返回结果按日期倒序（最新的在最前面），同日期按标题排序。
    /// </summary>
    public List<PostMeta> LoadAll()
    {
        var posts = new List<PostMeta>();
        var skipped = new List<string>();

        LastSkippedFiles = Array.Empty<string>();

        if (!Directory.Exists(PostsDirectory))
            return posts;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(PostsDirectory, "*.*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            throw new IOException($"无法读取文章目录：{ex.Message}", ex);
        }

        foreach (string file in files)
        {
            string ext = Path.GetExtension(file);
            if (!ext.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
                continue;

            // Hexo 会忽略以 _ 开头的文件，这里保持一致
            if (Path.GetFileName(file).StartsWith('_'))
                continue;

            try
            {
                posts.Add(ReadPost(file));
            }
            catch (Exception ex)
            {
                // 单个文件损坏/无权限：跳过，不影响列表；但记下来让界面能提示
                skipped.Add(file);
                LogService.Warn($"读取文章失败，已跳过：{file}（{ex.Message}）");
            }
        }

        LastSkippedFiles = skipped;

        return posts
            .OrderByDescending(p => p.Date ?? DateTime.MinValue)
            .ThenBy(p => p.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>读取单篇文章。</summary>
    public PostMeta ReadPost(string filePath)
    {
        string content = BlogSiteService.ReadTextUtf8(filePath);
        var fm = FrontMatter.Parse(content);

        var meta = new PostMeta
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            RelativePath = Path.GetRelativePath(PostsDirectory, filePath),
            Body = fm.Body,
            FrontMatter = fm
        };

        meta.Title = FrontMatter.GetScalar(fm, "title");
        if (string.IsNullOrWhiteSpace(meta.Title))
        {
            // 没有 title 字段时退化为文件名，保证列表里不是空白
            meta.Title = Path.GetFileNameWithoutExtension(filePath);
        }

        meta.DateText = FrontMatter.GetScalar(fm, "date");
        meta.Date = PostMeta.ParseDate(meta.DateText);
        meta.Updated = PostMeta.ParseDate(FrontMatter.GetScalar(fm, "updated"));

        // 没有 date 字段时用文件修改时间兜底，列表排序不会乱
        var info = new FileInfo(filePath);
        meta.FileModified = info.LastWriteTime;
        meta.FileSize = info.Length;
        meta.Date ??= info.LastWriteTime;

        meta.Tags = FrontMatter.GetList(fm, "tags");
        meta.Categories = FrontMatter.GetList(fm, "categories");
        meta.Top = FrontMatter.GetTop(fm);
        meta.Comments = FrontMatter.GetBool(fm, "comments", defaultValue: true);

        return meta;
    }

    // ==================================================================
    // 保存 / 新建
    // ==================================================================

    /// <summary>把元信息 + 正文写回文件（UTF-8 无 BOM，符合 Hexo 习惯）。</summary>
    public void Save(PostMeta meta, string body)
    {
        // 覆盖之前先留一份原文：改坏了、误删了都能退回来。
        // 备份失败不会阻断保存（内部已吞异常并记日志），保存本身的优先级更高。
        if (File.Exists(meta.FilePath))
            Backups.BackupBeforeSave(meta.FilePath, meta.RelativePath);

        meta.Body = body ?? string.Empty;

        string content = FrontMatter.Build(meta, meta.FrontMatter);
        WriteText(meta.FilePath, content);

        // 回读文件时间，让列表显示的修改时间与实际一致
        try
        {
            var info = new FileInfo(meta.FilePath);
            meta.FileModified = info.LastWriteTime;
            meta.FileSize = info.Length;
        }
        catch { }
    }

    /// <summary>
    /// 新建文章。
    /// </summary>
    /// <param name="fileNameWithoutExt">不含扩展名的文件名，已由调用方做过合法性处理。</param>
    /// <param name="meta">元信息（标题、日期、标签等）。</param>
    /// <param name="body">正文。</param>
    /// <returns>新建文件的完整路径。</returns>
    public string CreateNew(string fileNameWithoutExt, PostMeta meta, string body)
    {
        Directory.CreateDirectory(PostsDirectory);

        string safeName = SanitizeFileName(fileNameWithoutExt);
        string filePath = MakeUniquePath(PostsDirectory, safeName, ".md");

        meta.FilePath = filePath;
        meta.FileName = Path.GetFileName(filePath);
        meta.RelativePath = Path.GetFileName(filePath);
        meta.Body = body ?? string.Empty;

        // 新文章没有历史 front-matter，用空白结构即可
        meta.FrontMatter = new FrontMatterBlock();
        meta.FrontMatter.Exists = true;

        string content = FrontMatter.Build(meta, meta.FrontMatter);
        WriteText(filePath, content);

        return filePath;
    }

    // ==================================================================
    // 删除
    // ==================================================================

    /// <summary>删除文章（优先移入回收站，失败时提示调用方决定是否强删）。</summary>
    public bool Delete(PostMeta meta, out string error)
    {
        error = string.Empty;

        if (!File.Exists(meta.FilePath))
        {
            error = "文件已经不存在了。";
            return false;
        }

        if (RecycleBin.Delete(meta.FilePath, out string recycleError))
            return true;

        // 回收站不可用（例如网络盘）时，退化为直接删除，但要让调用方知道
        try
        {
            File.Delete(meta.FilePath);
            return true;
        }
        catch (Exception ex)
        {
            error = $"删除失败：{ex.Message}\n（回收站错误：{recycleError}）";
            return false;
        }
    }

    // ==================================================================
    // 图片转存
    // ==================================================================

    /// <summary>
    /// 把拖入的图片复制到 source/images/yyyy/MM/ 下，并返回可直接插入正文的 Markdown 片段。
    ///
    /// 为什么放 source/images 而不是文章同名目录？
    /// 因为 post_asset_folder 需要在 _config.yml 里显式开启，不是所有博客都开了。
    /// source/images 是 Hexo 通用约定，配合绝对路径 /images/xxx 在任何配置下都能正确显示。
    /// </summary>
    public string ImportImage(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("图片文件不存在。", sourcePath);

        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!IsImageExtension(ext))
            throw new InvalidOperationException("不支持的图片格式：" + ext);

        string subFolder = DateTime.Now.ToString("yyyy/MM");
        string destDir = Path.Combine(ImagesDirectory, subFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(destDir);

        string baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        if (baseName.Length == 0)
            baseName = "image";

        string destPath = MakeUniquePath(destDir, baseName, ext);
        File.Copy(sourcePath, destPath, overwrite: false);

        string fileName = Path.GetFileName(destPath);
        string urlPath = $"/images/{subFolder}/{fileName}";

        return $"![{Path.GetFileNameWithoutExtension(fileName)}]({urlPath})";
    }

    /// <summary>判断扩展名是否为常见图片格式。</summary>
    public static bool IsImageExtension(string extension)
    {
        return extension.ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".avif";
    }

    // ==================================================================
    // 工具方法
    // ==================================================================

    /// <summary>去掉 Windows 文件名里的非法字符，空格转连字符。</summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var sb = new StringBuilder();
        foreach (char c in name.Trim())
        {
            if (Path.GetInvalidFileNameChars().Contains(c))
                sb.Append('-');
            else if (c == ' ')
                sb.Append('-');
            else
                sb.Append(c);
        }

        string result = sb.ToString().Trim().TrimEnd('.');

        // 压缩连续的连字符：a---b → a-b
        while (result.Contains("--", StringComparison.Ordinal))
            result = result.Replace("--", "-", StringComparison.Ordinal);

        return result.Trim('-');
    }

    /// <summary>在指定目录下生成不冲突的文件路径：name.md → name-1.md → name-2.md …</summary>
    public static string MakeUniquePath(string directory, string baseName, string extension)
    {
        string candidate = Path.Combine(directory, baseName + extension);
        if (!File.Exists(candidate))
            return candidate;

        for (int i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(directory, $"{baseName}-{i}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        // 极端情况：用时间戳兜底
        return Path.Combine(directory, $"{baseName}-{DateTime.Now:yyyyMMddHHmmss}{extension}");
    }

    /// <summary>写 UTF-8 无 BOM 文本（主流程写文章/配置统一走这里）。</summary>
    public static void WriteText(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
