using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using GitLabPipelineMonitor.Core.Update;

namespace GitLabPipelineMonitor.Tests;

/// <summary>
/// 更新安装链路的回归测试。
///
/// 这里测的是「旧版本退出后，助手怎么把新文件换上去」——
/// 自动更新之前靠系统 PowerShell 干这件事，在装了 Windows Terminal 的机器上
/// 会弹出一个空白终端窗口杵着，看着像卡死；而且脚本一失败用户完全没反馈。
/// 现在换成了 C# 实现（<see cref="UpdateInstaller"/>），逻辑就能直接断言了。
/// </summary>
public static class UpdateInstallerTests
{
    public static Task Run()
    {
        NormalReplace();
        ReplaceFailureRollsBack();
        WaitForExitReturnsImmediatelyWhenProcessIsGone();
        return Task.CompletedTask;
    }

    /// <summary>正常替换：新文件就位、旧包和临时目录都清掉、新版本被启动。</summary>
    private static void NormalReplace()
    {
        var root = NewTempDir();
        try
        {
            var current = Path.Combine(root, "app.exe");
            var incoming = Path.Combine(root, "new", "app.exe");
            var work = Path.Combine(root, "work");
            Directory.CreateDirectory(Path.GetDirectoryName(incoming)!);
            Directory.CreateDirectory(work);
            File.WriteAllText(current, "old-version");
            File.WriteAllText(incoming, "new-version");

            string? started = null;
            var log = new List<string>();
            var ok = UpdateInstaller.Install(current, incoming, work,
                startNew: exe => started = exe,
                log: log.Add,
                settle: TimeSpan.Zero);

            Check.True(ok, "替换应当成功");
            Check.Equal("new-version", File.ReadAllText(current), "当前 exe 应当是新版内容");
            Check.False(File.Exists(current + UpdateInstaller.OldSuffix), "改名后的旧包应当删掉");
            Check.False(Directory.Exists(work), "临时工作目录应当删掉");
            Check.Equal(current, started ?? "", "新版本应当被启动");
            Check.True(log.Count >= 4, $"应当有日志记录（实际 {log.Count} 条）");
            Program.Ok("替换：新文件就位、旧包与临时目录清掉、新版本被启动");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>替换失败要能回滚：旧版本原样还在，并且被重新拉起来。</summary>
    private static void ReplaceFailureRollsBack()
    {
        var root = NewTempDir();
        try
        {
            var current = Path.Combine(root, "app.exe");
            var work = Path.Combine(root, "work");
            Directory.CreateDirectory(work);
            File.WriteAllText(current, "old-version");
            // 新文件不存在 —— 拷贝那一步必然失败

            string? started = null;
            var log = new List<string>();
            var ok = UpdateInstaller.Install(current, Path.Combine(root, "missing.exe"), work,
                startNew: exe => started = exe,
                log: log.Add,
                settle: TimeSpan.Zero);

            Check.False(ok, "替换应当失败");
            Check.Equal("old-version", File.ReadAllText(current), "失败后旧版本内容应当还在");
            Check.Equal(current, started ?? "", "失败后应当把旧版本重新拉起来");
            Check.True(log.Exists(line => line.Contains("替换失败")), "应当记录失败原因");
            Program.Ok("回滚：替换失败时旧版本原样还在并被重新拉起");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    /// <summary>进程早就不在了，等待要立刻返回而不是干等。</summary>
    private static void WaitForExitReturnsImmediatelyWhenProcessIsGone()
    {
        // 谁都不在的 pid：等待应当立刻返回
        var missing = UpdateInstaller.WaitForExit(int.MaxValue, TimeSpan.FromSeconds(30));
        Check.True(missing, "进程不存在应当立刻算作已退出");
        Program.Ok("等待退出：进程已不在时立刻返回，不干等");

        if (!OperatingSystem.IsWindows()) return;

        // 起一个马上就会退出的系统进程，等它退出后再拿它的 pid 来等
        var whoami = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        if (!File.Exists(whoami)) return;

        using var probe = Process.Start(new ProcessStartInfo(whoami) { UseShellExecute = false, CreateNoWindow = true });
        if (probe is null) return;
        probe.WaitForExit(10000);
        var pid = probe.Id;

        var stopwatch = Stopwatch.StartNew();
        var gone = UpdateInstaller.WaitForExit(pid, TimeSpan.FromSeconds(30));
        stopwatch.Stop();

        Check.True(gone, "已经退出的进程应当立刻算作已退出");
        Check.True(stopwatch.ElapsedMilliseconds < 3000, $"不该干等（实际等了 {stopwatch.ElapsedMilliseconds} ms）");
        Program.Ok("等待退出：已退出的进程立刻算作已退出");
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "glpm-updateinstaller-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { }
    }
}
