namespace GitLabPipelineMonitor.Core;

/// <summary>防抖：一段时间内多次调用只在最后一次之后执行一次。
/// 用于地址、令牌这类逐字输入的字段 —— 每敲一个字符就发一轮请求会把 GitLab 打爆，
/// 也会让面板反复闪空。</summary>
public sealed class Debouncer
{
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _current;
    private readonly object _gate = new();

    public Debouncer(TimeSpan delay) => _delay = delay;

    /// <summary>推迟执行；期间再次调用会重新计时。</summary>
    public void Debounce(Action action)
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            cancellation = new CancellationTokenSource();
            _current = cancellation;
        }
        _ = WaitAndRunAsync(cancellation, action);
    }

    private async Task WaitAndRunAsync(CancellationTokenSource cancellation, Action action)
    {
        try
        {
            await Task.Delay(_delay, cancellation.Token).ConfigureAwait(false);
            action();
        }
        catch (OperationCanceledException)
        {
            // 被后续调用取代，正常情况
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            _current = null;
        }
    }
}
