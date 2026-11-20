using System.IO;
using System.Windows.Threading;
using GitLabPipelineMonitor.Core;

namespace GitLabPipelineMonitor;

/// <summary>
/// 全局异常兜底。托盘常驻程序不该因为一处绑定或绘制错误就弹系统崩溃框退出 ——
/// 那会让人以为整个应用坏了。这里把未处理异常写进 error.log，界面继续跑，
/// 出问题时你把日志发我就能定位。
/// </summary>
public static class CrashGuard
{
    private static readonly object Gate = new();
    private static WindowsHost? _host;

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppInfo.Name, "error.log");

    public static void Install(WindowsHost host)
    {
        _host = host;

        // UI 线程上的异常：记下来并标记已处理，程序继续跑
        System.Windows.Application.Current.Dispatcher.UnhandledException += OnDispatcherUnhandledException;

        // 后台线程上的异常：无法阻止进程结束，但至少留下现场
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("后台线程", e.ExceptionObject as Exception, fatal: true);

        // async 里没 await 也没人看的异常（.NET 4.5 起默认会崩进程，这里只记录）
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("未观察的任务", e.Exception, fatal: false);
            e.SetObserved();
        };
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Write("UI 线程", e.Exception, fatal: false);
        e.Handled = true;
    }

    /// <summary>同一个异常反复触发时（比如绑定每帧都错）只记第一次，别把日志刷爆。</summary>
    private static string? _lastSignature;

    public static void Write(string where, Exception? error, bool fatal)
    {
        if (error is null) return;
        var signature = error.GetType().FullName + "|" + error.Message;
        lock (Gate)
        {
            if (signature == _lastSignature) return;
            _lastSignature = signature;
        }

        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}{(fatal ? "（致命）" : "")} · {AppInfo.Version} (build {AppInfo.Build})\n"
            + $"{error.GetType().Name}: {error.Message}\n{error.StackTrace}\n\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, text);
        }
        catch (Exception) { /* 日志写不了也不能让记日志本身把人崩了 */ }

        try { _host?.Notify(AppInfo.Name, $"出现一个错误，已记录到\n{LogPath}", "crash"); }
        catch (Exception) { }
    }
}
