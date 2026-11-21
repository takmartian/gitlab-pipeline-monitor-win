using System.Diagnostics;

namespace GitLabPipelineMonitor.Tests;

/// <summary>
/// 零依赖测试 runner：注册用例 → 逐个跑 → 打印结果，失败返回非零退出码。
/// 不用 xUnit/NUnit 是为了守住「零第三方依赖」这条底线 ——
/// 而且这些测试全是纯逻辑断言，一个 60 行的 runner 就够了。
/// 用法：dotnet run --project tests/GitLabPipelineMonitor.Tests</summary>
public static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    public static async Task<int> Main()
    {
        // 测试固定用中文语境跑：断言里的期望文案是按中文写的
        GitLabPipelineMonitor.Core.Localization.Loc.Language = GitLabPipelineMonitor.Core.Localization.AppLanguage.Zh;

        var suites = new (string Name, Func<Task> Run)[]
        {
            ("服务器地址规范化", UrlTests.Run),
            ("链接安全", UrlTests.RunSafeLinks),
            ("版本号比较", VersionTests.Run),
            ("更新说明清理", NotesTests.Run),
            ("更新安装（替换与回滚）", UpdateInstallerTests.Run),
            ("令牌加密存储", TokenStoreTests.Run),
            ("模型解析（宽松取值）", ModelTests.Run),
            ("状态与筛选", StatusTests.Run),
            ("API 客户端", ClientTests.Run),
            ("错误信息提取", ErrorTests.Run),
            ("格式化", FormatTests.Run),
            ("本地化", LocTests.Run),
            ("状态编排", StateTests.Run),
            ("屏幕贴边与命中", ScreenTests.Run),
            ("真实 GitHub Release 数据", LiveReleaseTests.Run)
        };

        var stopwatch = Stopwatch.StartNew();
        foreach (var (name, run) in suites)
        {
            Console.WriteLine();
            Console.WriteLine($"── {name}");
            try
            {
                await run();
            }
            catch (CheckFailed failure)
            {
                Failures.Add($"{name} › {failure.Message}");
                Console.WriteLine($"   ✗ {failure.Message}");
            }
            catch (Exception failure)
            {
                Failures.Add($"{name} › 意外异常 {failure.GetType().Name}: {failure.Message}");
                Console.WriteLine($"   ✗ 意外异常 {failure.GetType().Name}: {failure.Message}");
            }
        }
        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine(new string('=', 52));
        if (Failures.Count == 0)
        {
            Console.WriteLine($"✓ 全部通过：{_passed} 项断言，耗时 {stopwatch.ElapsedMilliseconds} ms");
            return 0;
        }
        Console.WriteLine($"✗ 失败 {Failures.Count} 项（通过 {_passed} 项）");
        foreach (var failure in Failures) Console.WriteLine($"   · {failure}");
        return 1;
    }

    /// <summary>记一条通过的断言。</summary>
    public static void Ok(string what)
    {
        _passed++;
        Console.WriteLine($"   ✓ {what}");
    }
}
