using System.Windows.Threading;

namespace GitLabPipelineMonitor;

/// <summary>
/// UI 线程调度的小工具。
///
/// Core（AppState / AppSettings）跑在后台线程上：网络请求在异步方法里，
/// 状态一改就抛 PropertyChanged —— 而订阅这些事件的界面代码会去动
/// ObservableCollection、TextBox、依赖属性，这些都只能在 UI 线程上做。
/// 跨线程硬来会得到两种报错：
///   · 「调用线程无法访问此对象，因为另一个线程拥有该对象」
///   · 「该类型的 CollectionView 不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改」
/// 所以统一从这儿搬回 UI 线程，而不是在每个订阅点各写各的。
/// </summary>
internal static class UiThread
{
    private static Dispatcher? Current => System.Windows.Application.Current?.Dispatcher;

    /// <summary>当前代码是不是已经跑在 UI 线程上。</summary>
    public static bool IsCurrent => Current?.CheckAccess() ?? true;

    /// <summary>把一段代码搬到 UI 线程执行（本来就在 UI 线程就直接执行）。</summary>
    public static void Run(Action action)
    {
        var dispatcher = Current;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return; }
        _ = dispatcher.BeginInvoke(action);
    }

    /// <summary>把一段异步代码搬到 UI 线程跑，并在 UI 线程上等它跑完。</summary>
    public static async Task RunAsync(Func<Task> body)
    {
        var dispatcher = Current;
        if (dispatcher is null || dispatcher.CheckAccess()) { await body(); return; }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.BeginInvoke(async () =>
        {
            try { await body(); completion.SetResult(true); }
            catch (Exception error) { completion.SetException(error); }
        });
        await completion.Task;
    }
}
