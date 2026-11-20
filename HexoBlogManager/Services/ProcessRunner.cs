using System.Diagnostics;
using System.IO;
using System.Text;

namespace HexoBlogManager.Services;

/// <summary>
/// 命令执行结果。
/// </summary>
/// <param name="ExitCode">进程退出码，0 表示成功；-1 表示启动失败或人为终止。</param>
/// <param name="StdOut">标准输出（已按 UTF-8 解码）。</param>
/// <param name="StdErr">标准错误（已按 UTF-8 解码）。</param>
/// <param name="TimedOut">是否因超时被强制结束。</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;

    /// <summary>把 stdout / stderr 合成一段，便于统一展示或做关键字匹配。</summary>
    public string CombinedOutput
    {
        get
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(StdOut)) sb.Append(StdOut);
            if (!string.IsNullOrWhiteSpace(StdErr))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(StdErr);
            }
            return sb.ToString().Trim();
        }
    }
}

/// <summary>
/// 统一的命令行执行工具（模块一先提供「一次性执行并拿到完整输出」的能力）。
///
/// 关键设计点（都是 Windows 上踩过的坑）：
/// 1) 通过 cmd.exe /c 执行，因为 hexo 在 Windows 上是 hexo.cmd 批处理，不能直接 CreateProcess。
/// 2) 命令前加 `chcp 65001>nul`，把控制台代码页切成 UTF-8，
///    否则 hexo / npm 输出的中文在 GBK 代码页下会变成乱码。
/// 3) UseShellExecute = false + 重定向三个流 + CreateNoWindow，
///    避免弹出黑窗口，也避免子进程等待输入而挂死。
/// 4) WorkingDirectory 直接传 Unicode 路径，中文路径、带空格路径都不会被拆词。
/// 5) 超时保护：默认 60 秒，超时强制 Kill 整棵进程树，避免 hexo server 之类常驻进程卡死界面。
/// </summary>
public static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 执行一条命令并等待结束。
    /// </summary>
    /// <param name="command">要执行的命令，例如 "hexo version"、"hexo generate"。</param>
    /// <param name="workingDirectory">工作目录；博客相关命令必须传博客根目录。</param>
    /// <param name="timeout">超时时间，null 用默认 60 秒。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<ProcessResult> RunAsync(
        string command,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new ProcessResult(-1, string.Empty, "命令为空。", false);

        // 命令里若有换行会被 cmd 截断，这里统一压成单行
        command = command.Replace("\r", " ").Replace("\n", " ").Trim();

        var workDir = workingDirectory;
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
            workDir = Environment.SystemDirectory;   // 兜底：System32 一定存在

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // 注意：/c 后面整体加引号，cmd 会剥掉最外层引号再解析
            Arguments = $"/c \"chcp 65001>nul && {command}\"",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,           // 重定向后立刻关闭，防止子进程等待输入
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // 例如系统策略禁止启动 cmd.exe、杀软拦截等
            return new ProcessResult(-1, string.Empty, $"无法启动命令解释器：{ex.Message}", false);
        }

        try { process.StandardInput.Close(); } catch { /* 忽略，关闭失败不影响 */ }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        var limit = timeout ?? DefaultTimeout;
        var waitTask = process.WaitForExitAsync(ct);
        var finished = await Task.WhenAny(waitTask, Task.Delay(limit, ct)).ConfigureAwait(false);

        bool timedOut = false;
        if (finished != waitTask)
        {
            timedOut = true;
            try
            {
                // Kill(entireProcessTree: true) 会把 cmd 及其子进程（node）一起干掉
                process.Kill(entireProcessTree: true);
            }
            catch { /* 进程可能已自己退出，忽略 */ }

            try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { }
        }

        string stdout = SafeRead(stdOutTask);
        string stderr = SafeRead(stdErrTask);

        if (timedOut)
            stderr = string.IsNullOrWhiteSpace(stderr)
                ? $"命令执行超过 {limit.TotalSeconds:F0} 秒，已被强制终止。"
                : stderr + $"\n命令执行超过 {limit.TotalSeconds:F0} 秒，已被强制终止。";

        int code = -1;
        try { code = process.HasExited ? process.ExitCode : -1; } catch { }

        return new ProcessResult(code, stdout, stderr, timedOut);
    }

    /// <summary>安全读取输出任务：即使被取消或进程被杀，也不让异常冒到界面层。</summary>
    private static string SafeRead(Task<string> task)
    {
        try
        {
            return task.Wait(TimeSpan.FromSeconds(5)) ? task.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
