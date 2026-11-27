using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HexoBlogManager.Services;

namespace HexoBlogManager;

/// <summary>
/// 应用程序入口。
/// 核心职责：
/// 1) 注册三层全局异常兜底（UI 线程 / 非 UI 线程 / 未观察的 Task 异常），
///    保证任何未预料到的错误都以中文提示呈现，而不是直接闪退。
/// 2) 单实例：重复启动时把已有窗口唤到前台，而不是开第二份。
/// 3) 创建并显示主窗口。
/// </summary>
public partial class App : Application
{
    // 单实例与「唤醒已有窗口」的跨进程信令。
    // Local\ 前缀表示只在当前登录会话内可见，够用且不需要管理员权限。
    private const string InstanceMutexName = @"Local\HexoBlogManager.SingleInstance";
    private const string ActivateEventName = @"Local\HexoBlogManager.Activate";

    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 全局异常兜底 ----
        // UI 线程异常：最常见（按钮点击、异步回调里抛错）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // 后台线程异常：例如 Task.Run 内部未捕获
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        // 未被 await 的 Task 异常
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 先把「本次运行是什么版本、什么系统」记进日志，出问题时能对上现场
        LogService.WriteSessionHeader(e.Args);

        base.OnStartup(e);

        // ---- 单实例保护 ----
        // 两个实例同时开，会各自缓存文章列表、各自写 settings.json，
        // 后保存的覆盖先保存的，而且用户完全看不到发生了什么。
        //
        // 带命令行参数的调试启动（--screenshot / --page / --publish 等）跳过这项检查，
        // 否则自动化验证会被正在运行的实例拦下来。
        bool isDebugRun = e.Args.Any(a => a.StartsWith("--", StringComparison.Ordinal));

        if (!isDebugRun && !TryEnterSingleInstance())
        {
            LogService.Info("已有实例在运行，本次启动不创建窗口，只唤醒已有窗口");
            SignalRunningInstance();
            Shutdown(0);
            return;
        }

        try
        {
            var window = new MainWindow();
            MainWindow = window;

            // 开发调试用：带 --screenshot <路径> 参数启动时，渲染界面并保存为 PNG 后退出。
            // 正常双击启动不会进入这里，不影响日常使用。
            // 注意：必须在 Show() 之前挂 Loaded 事件——Show() 是同步的，Loaded 会立刻触发，
            // 之后再注册就再也收不到通知了。
            string? initialPage = TryGetArg(e.Args, "--page");
            if (!string.IsNullOrWhiteSpace(initialPage))
                window.InitialPage = initialPage;

            string? editorFile = TryGetArg(e.Args, "--editor");
            if (!string.IsNullOrWhiteSpace(editorFile))
                window.DebugOpenEditor = editorFile;

            // 调试用：打开编辑器后顺便弹出「历史版本」窗口
            if (e.Args.Any(arg => arg.Equals("--history", StringComparison.OrdinalIgnoreCase)))
                Views.PostEditorWindow.DebugOpenHistory = true;

            string? autorun = TryGetArg(e.Args, "--autorun");
            if (!string.IsNullOrWhiteSpace(autorun))
                window.DebugAutorun = autorun;

            string? configTab = TryGetArg(e.Args, "--configtab");
            if (int.TryParse(configTab, out int tabIndex))
                Views.ConfigView.StartupTabIndex = tabIndex;

            if (e.Args.Any(arg => arg.Equals("--scrollend", StringComparison.OrdinalIgnoreCase)))
                Views.ConfigView.DebugScrollToEnd = true;

            if (e.Args.Any(arg => arg.Equals("--configdetail", StringComparison.OrdinalIgnoreCase)))
                Views.ConfigView.DebugShowDetail = true;

            if (e.Args.Any(arg => arg.Equals("--configsave", StringComparison.OrdinalIgnoreCase)))
                Views.ConfigView.DebugAutoSave = true;

            // 调试用：配置页加载后自动打开「历史备份」窗口
            if (e.Args.Any(arg => arg.Equals("--confighistory", StringComparison.OrdinalIgnoreCase)))
                Views.ConfigView.DebugOpenHistory = true;

            string? shotPath = TryGetArg(e.Args, "--screenshot");
            if (!string.IsNullOrWhiteSpace(shotPath))
            {
                window.SuppressCloseConfirm = true;
                Views.PublishView.DebugMode = true;   // 截图模式下不弹确认框，避免卡住自动化流程

                // 默认等 12 秒（够环境探测 + 一次短命令）；发布这类长流程可以用 --shotdelay 调大
                double delaySeconds = 12;
                string? delayArg = TryGetArg(e.Args, "--shotdelay");
                if (double.TryParse(delayArg, out double parsed) && parsed > 0 && parsed < 600)
                    delaySeconds = parsed;

                ScheduleScreenshot(window, shotPath, delaySeconds);
            }

            // 调试用：发布页打开后自动跑一次发布（配合 --screenshot 做端到端验证）
            if (e.Args.Any(arg => arg.Equals("--publish", StringComparison.OrdinalIgnoreCase)))
            {
                Views.PublishView.DebugMode = true;
                Views.PublishView.DebugAutoPublish = true;
            }

            // 调试用：发布页打开后自动跑一次「检测仓库」
            if (e.Args.Any(arg => arg.Equals("--check", StringComparison.OrdinalIgnoreCase)))
            {
                Views.PublishView.DebugMode = true;
                Views.PublishView.DebugAutoCheck = true;
            }

            // 调试用：把 GitHub API 指向本地 mock 服务，便于离线验证仓库检测 / Pages 开启逻辑
            string? apiBase = TryGetArg(e.Args, "--api-base");
            if (!string.IsNullOrWhiteSpace(apiBase))
                Services.GitHubApi.BaseUrl = apiBase;

            window.Show();

            // 只在「主实例」上监听唤醒信号，调试启动不注册，避免抢信号
            if (!isDebugRun)
                StartActivationListener(window);
        }
        catch (Exception ex)
        {
            LogService.Error("主窗口创建失败", ex);

            MessageBox.Show(
                $"程序启动失败。\n\n错误信息：{ex.Message}\n\n可能原因：\n" +
                "· 系统缺少 .NET 10 桌面运行时\n" +
                "· 程序文件被安全软件拦截\n\n" +
                $"日志已写入：{LogService.CurrentFile ?? LogService.LogDirectory}\n\n" +
                "详细信息：\n" + ex,
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>UI 线程未处理异常：写日志、提示，然后继续运行。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Error("UI 线程未处理异常", e.Exception);

        MessageBox.Show(
            $"操作过程中发生了一个未预期的错误。\n\n错误信息：{e.Exception.Message}\n\n" +
            "这个问题已经记录到日志里，反馈时附上日志会快很多。\n\n" +
            $"日志位置：{LogService.CurrentFile ?? LogService.LogDirectory}\n\n" +
            "详细堆栈：\n" + e.Exception,
            "出错了", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;   // 标记为已处理，程序不退出
    }

    /// <summary>非 UI 线程未处理异常：这种异常无法阻止进程终止，只能尽力留下信息。</summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        LogService.Error("非 UI 线程未处理异常（进程即将终止）", ex);

        MessageBox.Show(
            $"程序遇到严重错误，即将退出。\n\n错误信息：{ex?.Message ?? "未知错误"}\n\n" +
            $"日志已写入：{LogService.CurrentFile ?? LogService.LogDirectory}\n\n" +
            $"详细堆栈：\n{ex}",
            "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>未被观察的 Task 异常：记日志并标记已观察，避免进程被 .NET 终止。</summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogService.Error("未观察的 Task 异常", e.Exception);
        e.SetObserved();
    }

    // ==================================================================
    // 单实例
    // ==================================================================

    /// <summary>
    /// 尝试成为唯一实例。
    /// 拿不到互斥体说明已经有一份在跑；出现权限等意外时放行，
    /// 单实例是便利功能，不应该反过来让程序打不开。
    /// </summary>
    private static bool TryEnterSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);

            if (createdNew)
                return true;

            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }
        catch (Exception ex)
        {
            LogService.Warn("单实例检查失败，按允许启动处理：" + ex.Message);
            return true;
        }
    }

    /// <summary>通知已在运行的那份把自己唤到前台。</summary>
    private static void SignalRunningInstance()
    {
        // 存在竞争窗口：对方刚拿到互斥体，事件对象还没创建，所以重试几次
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
                {
                    using (handle)
                        handle.Set();

                    return;
                }
            }
            catch (Exception ex)
            {
                LogService.Warn("唤醒已有实例失败：" + ex.Message);
                return;
            }

            Thread.Sleep(120);
        }
    }

    /// <summary>
    /// 后台等唤醒信号；收到就把主窗口提到前台。
    /// 用独立线程而不是 UI 线程轮询，避免阻塞消息循环。
    /// </summary>
    private static void StartActivationListener(Window window)
    {
        try
        {
            _activateSignal = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                ActivateEventName);
        }
        catch (Exception ex)
        {
            LogService.Warn("无法创建唤醒信号，重复启动不会自动唤起窗口：" + ex.Message);
            return;
        }

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _activateSignal.WaitOne();
                    window.Dispatcher.BeginInvoke(new Action(() => BringToFront(window)));
                }
                catch
                {
                    return;   // 句柄被释放（程序正在退出）
                }
            }
        })
        {
            IsBackground = true,          // 后台线程，不阻止进程退出
            Name = "HexoBlogManager.ActivateListener"
        };

        thread.Start();
    }

    /// <summary>把窗口从最小化恢复并置顶激活。</summary>
    private static void BringToFront(Window window)
    {
        try
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Show();
            window.Activate();

            // 窗口被别的程序盖住时，Activate 不一定能拿到前台，
            // 短暂切一下 Topmost 更可靠（用完立刻还原，不改变用户设置）
            bool topmost = window.Topmost;
            window.Topmost = true;
            window.Topmost = topmost;

            window.Focus();

            LogService.Info("收到重复启动信号，已把已有窗口唤到前台");
        }
        catch (Exception ex)
        {
            LogService.Warn("唤醒窗口失败：" + ex.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _activateSignal?.Dispose(); } catch { }
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        try { _instanceMutex?.Dispose(); } catch { }

        base.OnExit(e);
    }

    // ==================================================================
    // 开发调试辅助：界面截图
    // ==================================================================

    private static string? TryGetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// 等界面渲染稳定（含异步环境检测）后，把窗口内容渲染成 PNG，然后退出程序。
    /// 用 RenderTargetBitmap 而不是屏幕抓取，好处是不受窗口遮挡、多显示器、DPI 缩放影响。
    /// </summary>
    private static void ScheduleScreenshot(MainWindow window, string outputPath, double delaySeconds = 12)
    {
        window.Loaded += async (_, _) =>
        {
            // 给异步探测（node / hexo / 目录校验）和可能自动执行的命令留出完成时间
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

            try
            {
                // 如果调试模式额外打开了别的窗口（例如编辑器），截取最后打开的那个
                Window target = Current.Windows
                    .OfType<Window>()
                    .Where(w => w.IsVisible && !ReferenceEquals(w, window))
                    .LastOrDefault() ?? window;

                var dpi = VisualTreeHelper.GetDpi(target);

                int width = (int)Math.Ceiling(target.ActualWidth * dpi.DpiScaleX);
                int height = (int)Math.Ceiling(target.ActualHeight * dpi.DpiScaleY);
                if (width <= 0 || height <= 0)
                    throw new InvalidOperationException("窗口尺寸为 0，无法截图。");

                var bitmap = new RenderTargetBitmap(
                    width, height,
                    96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
                    PixelFormats.Pbgra32);

                bitmap.Render(target);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                string? dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                using (var stream = File.Create(outputPath))
                    encoder.Save(stream);
            }
            catch (Exception ex)
            {
                // 调试模式下不弹窗（会阻塞自动化流程），把异常写到旁边的日志文件里
                try
                {
                    File.WriteAllText(outputPath + ".error.txt",
                        $"{ex}\n\n窗口尺寸：{window.ActualWidth} x {window.ActualHeight}\n" +
                        $"窗口状态：{window.WindowState}");
                }
                catch { }
            }
            finally
            {
                Current.Shutdown();
            }
        };
    }
}
