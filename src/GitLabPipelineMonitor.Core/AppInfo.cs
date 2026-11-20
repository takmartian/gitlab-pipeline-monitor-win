namespace GitLabPipelineMonitor.Core;

/// <summary>
/// 应用元信息。由宿主在启动时填一次（Core 不去读自身程序集元数据，
/// 这样测试里可以随意伪造版本，也便于把逻辑层单独编译到非 Windows 平台）。</summary>
public static class AppInfo
{
    public static string Name { get; set; } = "GitLab Pipeline Monitor";

    /// <summary>形如 1.0.0；诊断和更新检查都用它。</summary>
    public static string Version { get; set; } = "dev";

    /// <summary>构建号，每次发版 +1。</summary>
    public static string Build { get; set; } = "0";

    /// <summary>GitHub 仓库（更新源）。</summary>
    public const string Repo = "takmartian/gitlab-pipeline-monitor-win";

    /// <summary>更新检查地址。测试时可以整体换掉。</summary>
    public static string ReleaseApiUrl { get; set; } =
        "https://api.github.com/repos/" + Repo + "/releases/latest";
}
