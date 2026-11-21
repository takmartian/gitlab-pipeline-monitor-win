using System.Diagnostics;

namespace GitLabPipelineMonitor.Core.Update;

/// <summary>
/// 更新安装：把新版本的文件换到旧版本的位置上。
///
/// Windows 上正在运行的 exe **不能删、但能改名**，所以替换只能是两步走：
/// 先把旧文件改名腾出路径，再把新文件拷进来，然后启动新版本、删掉改名后的旧包。
/// 这套纯文件操作放在 Core 里（可测），主程序只负责起助手进程和写日志。
/// </summary>
public static class UpdateInstaller
{
    /// <summary>改名后的旧包用的后缀。</summary>
    public const string OldSuffix = ".old";

    /// <summary>
    /// 等某个进程从系统里消失。进程本来就不在了就立刻返回。
    /// 超时也返回 false —— 后面替换大概率会失败，但总比无限等下去强。
    /// </summary>
    public static bool WaitForExit(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (!IsAlive(pid)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// 换上新版本并启动它。
    /// </summary>
    /// <param name="currentExe">当前正在运行的 exe 路径。</param>
    /// <param name="newExe">新版本 exe 的路径。</param>
    /// <param name="workDirectory">下载解压出来的临时目录，换完删掉。</param>
    /// <param name="startNew">启动新版本的回调；不给就用 <see cref="Process.Start(string)"/>。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="settle">启动新版本后等一会儿再删旧包，给新进程留出启动时间。</param>
    /// <returns>替换是否成功。</returns>
    public static bool Install(
        string currentExe,
        string newExe,
        string workDirectory,
        Action<string>? startNew = null,
        Action<string>? log = null,
        TimeSpan? settle = null)
    {
        void Log(string message) => log?.Invoke(message);
        void Start(string exe) => (startNew ?? DefaultStart)(exe);

        var oldExe = currentExe + OldSuffix;
        try
        {
            if (File.Exists(oldExe)) SafeDelete(oldExe);

            // 运行中的 exe 可以改名、不能删除 —— 先改名腾出路径
            File.Move(currentExe, oldExe, true);
            Log("旧文件已改名");

            File.Copy(newExe, currentExe, true);
            Log("新文件已就位");

            Start(currentExe);
            Log("新版本已启动");

            Thread.Sleep(settle ?? TimeSpan.FromSeconds(2));

            SafeDelete(oldExe);
            Log("旧文件已清理");
            SafeDeleteDirectory(workDirectory);
            Log("工作目录已清理");
            return true;
        }
        catch (Exception error)
        {
            // 替换失败至少把人叫回来：把旧包改回去再启动
            Log($"替换失败：{error.GetType().Name}: {error.Message}");
            try
            {
                if (File.Exists(oldExe)) File.Move(oldExe, currentExe, true);
            }
            catch (Exception rollback)
            {
                Log($"回滚也失败了：{rollback.Message}");
            }

            try { Start(currentExe); } catch (Exception start) { Log($"旧版本也没能启动：{start.Message}"); }
            return false;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;   // 进程已经不在了 —— 正是我们要的
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void DefaultStart(string exe) => Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
