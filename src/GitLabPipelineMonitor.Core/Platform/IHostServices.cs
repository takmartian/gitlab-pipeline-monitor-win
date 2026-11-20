namespace GitLabPipelineMonitor.Core.Platform;

/// <summary>
/// 宿主（WPF 应用）注入的平台能力。Core 项目只依赖这些接口，
/// 于是全部业务逻辑都能脱离 Windows 编译和测试 —— 这是这个项目能在 macOS 上验证的原因。
/// </summary>
public interface IHostServices
{
    /// <summary>机器指纹：Windows 用 MachineGuid。令牌加密密钥的一部分，本身不落盘。</summary>
    string MachineFingerprint { get; }

    /// <summary>当前登录用户名，参与令牌加密密钥派生。</summary>
    string UserName { get; }

    /// <summary>在浏览器里打开链接；实现方要保证只接受 http(s)。</summary>
    void OpenUrl(string url);

    /// <summary>把文本放进剪贴板。</summary>
    void CopyToClipboard(string text);

    /// <summary>系统通知是否可用（Windows 上托盘气泡基本总是可用，留这个口子是为了和 macOS 版对齐语义）。</summary>
    bool NotificationsAvailable { get; }

    /// <summary>发一条系统通知。id 相同的通知会互相替换。</summary>
    void Notify(string title, string body, string id);

    /// <summary>开机自启状态（Windows 用注册表 Run 键）。</summary>
    bool LaunchAtLogin { get; set; }
}
