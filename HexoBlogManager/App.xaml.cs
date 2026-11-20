using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HexoBlogManager;

/// <summary>
/// 应用程序入口。
/// 核心职责：
/// 1) 注册三层全局异常兜底（UI 线程 / 非 UI 线程 / 未观察的 Task 异常），
///    保证任何未预料到的错误都以中文提示呈现，而不是直接闪退。
/// 2) 创建并显示主窗口。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 全局异常兜底 ----
        // UI 线程异常：最常见（按钮点击、异步回调里抛错）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // 后台线程异常：例如 Task.Run 内部未捕获
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        // 未被 await 的 Task 异常
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

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

            string? autorun = TryGetArg(e.Args, "--autorun");
            if (!string.IsNullOrWhiteSpace(autorun))
                window.DebugAutorun = autorun;

            string? configTab = TryGetArg(e.Args, "--configtab");
            if (int.TryParse(configTab, out int tabIndex))
                Views.ConfigView.StartupTabIndex = tabIndex;

            if (e.Args.Any(arg => arg.Equals("--scrollend", StringComparison.OrdinalIgnoreCase)))
                Views.ConfigView.DebugScrollToEnd = true;

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
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"程序启动失败。\n\n错误信息：{ex.Message}\n\n可能原因：\n" +
                "· 系统缺少 .NET 10 桌面运行时\n" +
                "· 程序文件被安全软件拦截\n\n详细信息：\n" + ex,
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>UI 线程未处理异常：提示后继续运行，避免整个程序退出。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"操作过程中发生了一个未预期的错误。\n\n错误信息：{e.Exception.Message}\n\n" +
            "如果这个问题可以稳定复现，请把上面的信息记下来。\n\n详细堆栈：\n" + e.Exception,
            "出错了", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;   // 标记为已处理，程序不退出
    }

    /// <summary>非 UI 线程未处理异常：这种异常无法阻止进程终止，只能尽力留下信息。</summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        MessageBox.Show(
            $"程序遇到严重错误，即将退出。\n\n错误信息：{ex?.Message ?? "未知错误"}\n\n详细堆栈：\n{ex}",
            "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>未被观察的 Task 异常：标记为已观察，避免进程被 .NET 终止。</summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
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
