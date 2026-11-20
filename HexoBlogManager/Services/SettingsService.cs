using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using HexoBlogManager.Models;

namespace HexoBlogManager.Services;

/// <summary>
/// 应用设置的读写服务。
///
/// 存储位置：%AppData%\HexoBlogManager\settings.json
/// 例如：C:\Users\xxx\AppData\Roaming\HexoBlogManager\settings.json
///
/// 容错策略：
/// · 文件不存在 → 返回默认设置，不报错（首次启动的正常情况）
/// · JSON 损坏 → 把损坏文件改名备份为 settings.json.bad，然后返回默认设置
/// · 写入失败（磁盘只读、被杀软锁住）→ 返回 false + 中文错误信息，由界面提示，不抛异常
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 让中文按原样写入，而不是变成 \u5B9D\u5B9D 这种，方便用户自己打开文件查看
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public string SettingsDirectory { get; }
    public string SettingsFilePath { get; }

    public SettingsService()
    {
        SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HexoBlogManager");
        SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");
    }

    /// <summary>读取设置。任何异常都降级为默认值，保证程序能启动。</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
                return new AppSettings();

            string json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
                return new AppSettings();

            settings.RecentPaths ??= new List<string>();
            return settings;
        }
        catch
        {
            // 文件损坏：备份一份，避免用户上次的路径被直接覆盖掉
            try
            {
                if (File.Exists(SettingsFilePath))
                    File.Copy(SettingsFilePath, SettingsFilePath + ".bad", overwrite: true);
            }
            catch { /* 备份失败也不影响启动 */ }

            return new AppSettings();
        }
    }

    /// <summary>保存设置。返回是否成功，失败时通过 error 输出中文原因。</summary>
    public bool Save(AppSettings settings, out string error)
    {
        error = string.Empty;
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            // 先写临时文件再替换，避免写入过程中断电/崩溃留下半个文件
            string tempPath = SettingsFilePath + ".tmp";
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsFilePath, overwrite: true);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"没有写入权限：{ex.Message}\n路径：{SettingsDirectory}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"保存设置失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 记住一个博客路径：更新主路径 + 维护最近列表。
    /// 去重按「忽略大小写 + 忽略末尾斜杠」比较，避免 D:\blog 和 D:\Blog\ 被存成两条。
    /// </summary>
    public void RememberBlogPath(string blogPath)
    {
        if (string.IsNullOrWhiteSpace(blogPath))
            return;

        var settings = Load();
        string normalized = NormalizeForCompare(blogPath);

        settings.BlogPath = blogPath;
        settings.LastOpenedAt = DateTimeOffset.Now;

        settings.RecentPaths.RemoveAll(p => NormalizeForCompare(p) == normalized);
        settings.RecentPaths.Insert(0, blogPath);

        // 最近列表最多保留 8 条
        if (settings.RecentPaths.Count > 8)
            settings.RecentPaths = settings.RecentPaths.Take(8).ToList();

        Save(settings, out _);   // 保存失败不阻塞主流程
    }

    /// <summary>
    /// 读取 GitHub 发布配置。文件里没有这一段时返回一份默认配置（不写盘）。
    /// </summary>
    public GitHubPublishSettings LoadGitHub()
    {
        var settings = Load();
        return settings.GitHub ?? new GitHubPublishSettings();
    }

    /// <summary>
    /// 保存 GitHub 发布配置。
    /// 注意：必须「读—改—写」，否则会把 BlogPath、RecentPaths 一起覆盖掉。
    /// </summary>
    public bool SaveGitHub(GitHubPublishSettings publish, out string error)
    {
        var settings = Load();
        settings.GitHub = publish;
        return Save(settings, out error);
    }

    /// <summary>归一化路径，仅用于比较是否「同一个目录」。</summary>
    public static string NormalizeForCompare(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
                       .TrimEnd('\\', '/')
                       .ToLowerInvariant();
        }
        catch
        {
            return (path ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}
