using System.Diagnostics;
using System.IO;
using System.Text;

namespace HexoBlogManager.Services;

/// <summary>
/// 可流式读取输出的进程封装（用于 git 这类需要边执行边看日志的命令）。
///
/// 与 HexoCommandRunner 的区别：
/// · HexoCommandRunner 是给「hexo 命令」定制的，会把所有 stderr 当错误；
/// · 本类允许外部注入「日志分级规则」，因为 git 的 stderr 其实是正常进度输出
///   （比如 "Writing objects: 100% ..."），当成错误会误导用户。
///
/// 与 ProcessRunner 的区别：本类能实时抛日志、能中途停止、能设超时。
/// </summary>
public sealed class StreamingProcess : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;

    /// <summary>输出一行日志（在后台读取线程触发）。</summary>
    public event EventHandler<LogEventArgs>? OutputReceived;

    /// <summary>外部注入的分级规则；不设置时用内置规则（fatal/error/warning 前缀）。</summary>
    public Func<string, LogLevel>? Classifier { get; set; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                try { return _process is not null && !_process.HasExited; }
                catch { return false; }
            }
        }
    }

    /// <summary>启动一条命令。</summary>
    public bool Start(string command, string workingDirectory, out string error)
    {
        error = string.Empty;

        if (IsRunning)
        {
            error = "已有命令正在执行，请稍候。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            error = $"工作目录不存在：{workingDirectory}";
            return false;
        }

        // 命令里不能有换行，否则 cmd 会把它截断
        command = command.Replace("\r", " ").Replace("\n", " ").Trim();

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // chcp 65001：先把代码页切成 UTF-8，hexo / git 的中文输出才不会乱码
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

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                OutputReceived?.Invoke(this, new LogEventArgs(Classify(e.Data), e.Data));
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

        try { process.StandardInput.Close(); } catch { }

        lock (_gate)
            _process = process;

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return true;
    }

    /// <summary>
    /// 等待命令结束。
    /// 用同步 WaitForExit 包在 Task.Run 里，是为了让异步读取流把剩余输出吐完再返回
    /// （WaitForExitAsync 不等读取完成，日志会缺尾巴）。
    /// </summary>
    public async Task<(int Code, bool TimedOut)> WaitForExitAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Process? process;
        lock (_gate)
            process = _process;

        if (process is null)
            return (-1, false);

        var exitTask = Task.Run(() =>
        {
            try { process.WaitForExit(); } catch { /* 进程可能已被杀 */ }
        }, CancellationToken.None);

        var delayTask = Task.Delay(timeout, ct);
        var completed = await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false);

        bool timedOut = false;

        if (completed != exitTask)
        {
            ct.ThrowIfCancellationRequested();
            timedOut = true;
            Stop();
            await exitTask.ConfigureAwait(false);
        }

        int code = -1;
        try { code = process.HasExited ? process.ExitCode : -1; } catch { }

        return (timedOut ? -1 : code, timedOut);
    }

    /// <summary>强制结束进程树（cmd 及其子进程）。</summary>
    public void Stop()
    {
        Process? process;
        lock (_gate)
            process = _process;

        if (process is null) return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private LogLevel Classify(string line)
    {
        if (Classifier is not null)
            return Classifier(line);

        string t = AnsiText.Strip(line).TrimStart();

        if (t.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;

        if (t.StartsWith("warning:", StringComparison.OrdinalIgnoreCase))
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
