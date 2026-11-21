using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using GitLabPipelineMonitor.Core.Update;

namespace GitLabPipelineMonitor;

/// <summary>
/// 更新助手：主程序退出后，由它接手把旧 exe 换成新 exe。
///
/// 为什么要它：Windows 上正在运行的 exe 不能被删除，但**可以改名**。
/// 所以顺序只能是「主程序退出 → 助手改名旧文件 → 拷入新文件 → 启动新版本 → 删掉改名后的旧文件」。
/// 助手必须是**另一个进程**，否则主程序自己还没退出，文件还被占着。
///
/// 为什么用主程序自己的另一个实例，而不是系统 PowerShell：
/// - 不弹窗。WinExe 启动不分配控制台，用 ``UseShellExecute=false`` +
///   ``CreateNoWindow=true`` 起就完全看不见；而 ``powershell.exe`` 会被
///   Windows Terminal 接管，``-WindowStyle Hidden`` 被忽略，用户只看到一个
///   空白终端杵在那儿，还以为程序卡死了。
/// - 不依赖系统自带的外壳。Windows 10 以后的 PowerShell 版本、默认终端宿主
///   行为都可能变，脚本一失败就更新不了，而且失败时用户什么都看不到。
/// - 逻辑在 C# 里（<see cref="UpdateInstaller"/>），能直接测。
///
/// 用法：``GitLabPipelineMonitor.exe --install-helper &lt;当前exe&gt; &lt;新exe&gt; &lt;工作目录&gt; &lt;等待退出的进程id&gt;``。
/// 这个模式必须在单实例互斥体之前处理，否则助手会被自己的单实例检查挡掉。
/// </summary>
internal static class UpdateHelper
{
    internal const string Switch = "--install-helper";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>是不是助手模式。是的话就把活干完并返回 true，调用方随后退出。</summary>
    public static bool TryRun(string[] args)
    {
        var index = Array.IndexOf(args, Switch);
        if (index < 0) return false;
        if (index + 4 >= args.Length) return false;

        var currentExe = args[index + 1];
        var newExe = args[index + 2];
        var workDirectory = args[index + 3];
        if (!int.TryParse(args[index + 4], out var waitForPid)) return false;

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitLab Pipeline Monitor", "update-install.log");
        void Log(string message)
        {
            TryAppend(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }

        Log($"助手启动：等待 pid {waitForPid} 退出");
        if (!UpdateInstaller.WaitForExit(waitForPid, WaitTimeout))
        {
            Log("等待主程序退出超时，接着试（替换失败会回滚）");
        }

        UpdateInstaller.Install(currentExe, newExe, workDirectory, log: Log);
        return true;
    }

    private static void TryAppend(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
