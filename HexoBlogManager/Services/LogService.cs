using System.IO;
using System.Text;

namespace HexoBlogManager.Services;

/// <summary>
/// 轻量文件日志。
///
/// 为什么要它：程序有全局异常兜底，但只弹 MessageBox。用户遇到问题时
/// 只能口述现象，支持方拿不到堆栈。日志落盘之后，"出错"这件事就有了证据。
///
/// 设计取舍：
/// 1) 不引日志框架（NLog / Serilog），几十行足够，也符合项目零依赖的约定；
/// 2) 任何写入失败都被吞掉——日志系统本身绝不能成为新的崩溃源；
/// 3) 按天分文件，单文件超过 4 MB 自动换序号，超过 30 天的自动清理。
/// </summary>
public static class LogService
{
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int KeepDays = 30;

    private static readonly object Sync = new();
    private static bool _cleanupDone;

    /// <summary>日志目录：%AppData%\HexoBlogManager\logs。</summary>
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HexoBlogManager", "logs");

    /// <summary>当前进程正在写的日志文件（首次写入后才确定）。</summary>
    public static string? CurrentFile { get; private set; }

    /// <summary>写一条普通信息。</summary>
    public static void Info(string message) => Write("INFO", message, null);

    /// <summary>写一条警告。</summary>
    public static void Warn(string message) => Write("WARN", message, null);

    /// <summary>写一条带堆栈的错误。</summary>
    public static void Error(string context, Exception? ex)
        => Write("ERROR", context + (ex is null ? string.Empty : "：" + ex.Message), ex);

    /// <summary>
    /// 写日志。内部吞掉所有异常：磁盘满、目录被占、权限不足都不影响主流程。
    /// </summary>
    public static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Sync)
            {
                CleanupOnce();

                string path = ResolveFile();
                var sb = new StringBuilder();

                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                  .Append("  [").Append(level).Append("]  ")
                  .Append(message)
                  .AppendLine();

                if (ex is not null)
                    sb.AppendLine(ex.ToString());

                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                CurrentFile = path;
            }
        }
        catch
        {
            // 日志写不进去就算了，绝不能让调用方因此失败
        }
    }

    /// <summary>本次运行的开场记录：版本、系统、命令行，便于对照问题现场。</summary>
    public static void WriteSessionHeader(string[] args)
    {
        try
        {
            string version = typeof(LogService).Assembly.GetName().Version?.ToString() ?? "unknown";

            Write("INFO", "========== 启动 ==========", null);
            Write("INFO", $"版本 {version}  系统 {Environment.OSVersion}  64位进程 {Environment.Is64BitProcess}", null);
            Write("INFO", $"命令行 {string.Join(' ', args)}", null);
        }
        catch { }
    }

    /// <summary>在资源管理器里打开日志目录；不存在就先建出来。</summary>
    public static bool OpenLogFolder(out string error)
    {
        error = string.Empty;

        try
        {
            Directory.CreateDirectory(LogDirectory);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LogDirectory,
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>日志目录里是否已经有内容（决定界面是否显示「打开日志」入口）。</summary>
    public static bool HasLogs()
    {
        try
        {
            return Directory.Exists(LogDirectory) &&
                   Directory.EnumerateFiles(LogDirectory, "*.log").Any();
        }
        catch
        {
            return false;
        }
    }

    // ==================================================================
    // 内部
    // ==================================================================

    /// <summary>选出今天该写的文件；超过大小上限就顺延序号。</summary>
    private static string ResolveFile()
    {
        Directory.CreateDirectory(LogDirectory);

        string date = DateTime.Now.ToString("yyyyMMdd");

        for (int i = 1; i < 100; i++)
        {
            string name = i == 1 ? $"app-{date}.log" : $"app-{date}-{i}.log";
            string path = Path.Combine(LogDirectory, name);

            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxFileBytes)
                return path;
        }

        // 一天写满 100 个文件属于异常情况，直接覆盖最后一个
        return Path.Combine(LogDirectory, $"app-{date}-overflow.log");
    }

    /// <summary>每次进程只清理一次过期日志。</summary>
    private static void CleanupOnce()
    {
        if (_cleanupDone) return;
        _cleanupDone = true;

        try
        {
            if (!Directory.Exists(LogDirectory)) return;

            DateTime cutoff = DateTime.Now.AddDays(-KeepDays);

            foreach (string file in Directory.EnumerateFiles(LogDirectory, "app-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }
}
