using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HexoBlogManager.Services;

/// <summary>日志级别，决定控制台里的文字颜色。</summary>
public enum LogLevel
{
    /// <summary>普通信息。</summary>
    Info,
    /// <summary>成功 / 完成。</summary>
    Success,
    /// <summary>警告。</summary>
    Warning,
    /// <summary>错误。</summary>
    Error,
    /// <summary>本工具自己输出的提示（例如 "$ hexo generate"）。</summary>
    System
}

public sealed class LogEventArgs : EventArgs
{
    public LogEventArgs(LogLevel level, string text)
    {
        Level = level;
        Text = text;
    }

    public LogLevel Level { get; }
    public string Text { get; }
}

/// <summary>
/// Hexo 命令执行器（流式输出版）。
///
/// 与模块一的 ProcessRunner 的区别：
/// · ProcessRunner 是「执行完再拿全部输出」，适合版本探测这类短命令；
/// · 本类边执行边推送输出，适合 generate / server 这类需要实时看进度的命令，
///   并且支持中途停止（hexo server 是常驻进程，必须能杀掉）。
///
/// 停止逻辑：Kill(entireProcessTree: true)，连同 cmd.exe 下挂着的 node 子进程一起结束。
/// </summary>
public sealed class HexoCommandRunner : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;

    /// <summary>输出一行日志（可能在后台线程触发，订阅方需要切回 UI 线程）。</summary>
    public event EventHandler<LogEventArgs>? OutputReceived;

    /// <summary>进程退出，参数是退出码。</summary>
    public event EventHandler<int>? ProcessExited;

    /// <summary>用户是否主动点了「停止」。</summary>
    public bool StopRequested { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _process is not null && !_process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    /// <summary>启动一条命令（异步推送输出，不阻塞调用线程）。</summary>
    /// <param name="command">例如 "hexo generate"。</param>
    /// <param name="workingDirectory">博客根目录。</param>
    /// <param name="error">启动失败的原因。</param>
    public bool Start(string command, string workingDirectory, out string error)
    {
        error = string.Empty;

        if (IsRunning)
        {
            error = "已有命令正在执行，请先停止或等待完成。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            error = "博客目录不存在，无法执行命令。";
            return false;
        }

        command = command.Replace("\r", " ").Replace("\n", " ").Trim();
        StopRequested = false;

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"chcp 65001>nul && {command}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                OutputReceived?.Invoke(this, new LogEventArgs(Classify(e.Data), e.Data));
        };

        // stderr 一律按错误处理，但不会中断执行（hexo 有时把进度信息也写 stderr）
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                OutputReceived?.Invoke(this, new LogEventArgs(LogLevel.Error, e.Data));
        };

        process.Exited += (_, _) =>
        {
            int code;
            try { code = process.ExitCode; }
            catch { code = -1; }

            ProcessExited?.Invoke(this, code);

            try { process.Dispose(); } catch { }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            error = $"无法启动命令：{ex.Message}";
            process.Dispose();
            return false;
        }

        // 关闭 stdin，避免个别命令等待输入导致挂起
        try { process.StandardInput.Close(); } catch { }

        lock (_gate)
            _process = process;

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return true;
    }

    /// <summary>停止当前命令（连同子进程）。</summary>
    public void Stop()
    {
        Process? process;
        lock (_gate)
            process = _process;

        if (process is null)
            return;

        StopRequested = true;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能刚好自己退出了，忽略
        }
    }

    /// <summary>从一行输出里提取本地预览地址（http://localhost:4000 之类）。</summary>
    public static string? TryExtractLocalUrl(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // 必须先剥掉 ANSI 颜色码：hexo 输出是 "\u001b[32mINFO ... http://localhost:4000/\u001b[39m"，
        // 直接正则匹配会把 "\u001b[39m" 里的 "[39m" 当成 URL 的一部分。
        line = AnsiText.Strip(line);

        var match = Regex.Match(line, @"https?://(?:localhost|127\.0\.0\.1)(?::\d+)?(?:/[^\s""'\[\]]*)?",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Value.TrimEnd('.', ',', ')', '，', '。') : null;
    }

    /// <summary>按行首关键字判定日志级别。</summary>
    private static LogLevel Classify(string line)
    {
        // 先剥掉 ANSI 颜色码，否则行首是 "\u001b[32mINFO"，匹配不到 "INFO"
        string trimmed = AnsiText.Strip(line).TrimStart();

        if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("FATAL", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Unhandled", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;

        if (trimmed.StartsWith("WARN", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warning;

        return LogLevel.Info;
    }

    public void Dispose()
    {
        Stop();

        lock (_gate)
        {
            try { _process?.Dispose(); } catch { }
            _process = null;
        }
    }
}
