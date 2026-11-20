using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.Platform;
using Microsoft.Win32;

namespace GitLabPipelineMonitor;

/// <summary>
/// Windows 平台能力实现。Core 只认接口，所以全部业务逻辑都能在 macOS 上编译和测试。
/// </summary>
public sealed class WindowsHost : IHostServices
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "GitLabPipelineMonitor";

    private string? _fingerprint;

    /// <summary>
    /// 机器指纹取 Windows 的 MachineGuid（安装系统时生成，重装前稳定）。
    /// 它是令牌加密密钥的一部分，本身不落盘。
    /// </summary>
    public string MachineFingerprint
    {
        get
        {
            if (_fingerprint is not null) return _fingerprint;
            var guid = ReadMachineGuid();
            // 极端情况下读不到（受限环境）：退回一个存在本地文件的随机密钥，
            // 至少保证同一台机器上能自洽加解密
            _fingerprint = guid is not null ? "mg:" + guid : "rk:" + LocalRandomKey();
            return _fingerprint;
        }
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string LocalRandomKey()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppInfo.Name, ".machine-key");
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length > 0) return existing;
            }
        }
        catch (Exception) { /* 读不到就重新生成 */ }

        var fresh = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, fresh);
        }
        catch (Exception) { /* 写失败本次运行内仍然可用 */ }
        return fresh;
    }

    public string UserName => Environment.UserName;

    public void OpenUrl(string url)
    {
        var safe = ServerAddress.SafeBrowserUrl(url);
        if (safe is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(safe)
            {
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // 没有关联浏览器时不弹错误框打扰人，静默忽略
        }
    }

    public void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, true);
        }
        catch (COMException)
        {
            // 剪贴板被别的进程锁住（很常见），重试一次
            try { Clipboard.SetDataObject(text, true); } catch (COMException) { }
        }
    }

    public bool NotificationsAvailable => true;

    /// <summary>托盘气泡通知。托盘图标还没建好时先攒着，建好后补发。</summary>
    public event Action<string, string, string>? NotifyRequested;

    public void Notify(string title, string body, string id) => NotifyRequested?.Invoke(title, body, id);

    /// <summary>开机自启写当前用户的 Run 键 —— Windows 上这就是标准做法，不需要引库。</summary>
    public bool LaunchAtLogin
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(AppValueName) is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (key is null) return;
                if (value) key.SetValue(AppValueName, $"\"{Environment.ProcessPath}\"");
                else key.DeleteValue(AppValueName, false);
            }
            catch (Exception)
            {
                // 注册表被策略锁住时不弹错误框
            }
        }
    }
}
