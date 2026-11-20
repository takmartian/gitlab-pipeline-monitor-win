namespace GitLabPipelineMonitor.Tests;

/// <summary>断言失败。带着用例名抛出，方便一眼看出是哪条挂了。</summary>
public sealed class CheckFailed : Exception
{
    public CheckFailed(string message) : base(message) { }
}

public static class Check
{
    public static void True(bool condition, string what)
    {
        if (!condition) throw new CheckFailed($"期望为真：{what}");
    }

    public static void False(bool condition, string what)
    {
        if (condition) throw new CheckFailed($"期望为假：{what}");
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new CheckFailed($"{what}：期望 [{Show(expected)}]，实际 [{Show(actual)}]");
    }

    public static void NotNull(object? value, string what)
    {
        if (value is null) throw new CheckFailed($"{what}：期望非空，实际为 null");
    }

    public static void IsNull(object? value, string what)
    {
        if (value is not null) throw new CheckFailed($"{what}：期望 null，实际 [{Show(value)}]");
    }

    public static void Contains(string needle, string haystack, string what)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
            throw new CheckFailed($"{what}：期望包含 [{needle}]，实际 [{haystack}]");
    }

    public static void NotContains(string needle, string haystack, string what)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
            throw new CheckFailed($"{what}：期望不含 [{needle}]，实际 [{haystack}]");
    }

    public static async Task<T> Throws<T>(Func<Task> body, string what) where T : Exception
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (T expected)
        {
            return expected;   // 就是要把它交出去，让调用方检查 Kind/Message
        }
        catch (Exception other)
        {
            throw new CheckFailed($"{what}：期望抛出 {typeof(T).Name}，实际抛出 {other.GetType().Name}: {other.Message}");
        }
        throw new CheckFailed($"{what}：期望抛出 {typeof(T).Name}，实际没有抛出");
    }

    public static void ThrowsNow<T>(Action body, string what) where T : Exception
    {
        try
        {
            body();
        }
        catch (T)
        {
            return;
        }
        catch (Exception other)
        {
            throw new CheckFailed($"{what}：期望抛出 {typeof(T).Name}，实际抛出 {other.GetType().Name}: {other.Message}");
        }
        throw new CheckFailed($"{what}：期望抛出 {typeof(T).Name}，实际没有抛出");
    }

    private static string Show(object? value) => value switch
    {
        null => "null",
        string s => s.Length == 0 ? "<空串>" : s,
        _ => value.ToString() ?? "<?>"
    };
}
