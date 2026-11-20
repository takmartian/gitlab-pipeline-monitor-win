namespace GitLabPipelineMonitor.Core.Localization;

public enum AppLanguage
{
    /// <summary>跟随系统。</summary>
    Auto,
    Zh,
    En
}

/// <summary>
/// 轻量本地化：代码里写中文原文，英文环境下查表翻译，查不到就退回中文。
/// 新增文案时最坏情况只是英文下显示中文，不会出现 key 占位符。
/// </summary>
public static class Loc
{
    public static AppLanguage Language { get; set; } = AppLanguage.Auto;

    public static AppLanguage Resolved => Language switch
    {
        AppLanguage.Zh or AppLanguage.En => Language,
        // 跟随系统：取系统 UI 语言，中文系一律算中文
        _ => IsSystemChinese() ? AppLanguage.Zh : AppLanguage.En
    };

    private static bool? _systemChinese;

    private static bool IsSystemChinese()
    {
        if (_systemChinese.HasValue) return _systemChinese.Value;
        var name = System.Globalization.CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
        _systemChinese = name.StartsWith("zh", StringComparison.Ordinal);
        return _systemChinese.Value;
    }

    /// <summary>测试用：清掉系统语言的缓存。</summary>
    public static void ResetSystemLanguageCache() => _systemChinese = null;

    public static bool IsEnglish => Resolved == AppLanguage.En;

    /// <summary>取译文：中文环境直接返回原文，英文环境查表，查不到退回中文。</summary>
    public static string T(string zh) => IsEnglish && English.Map.TryGetValue(zh, out var value) ? value : zh;

    /// <summary>带占位符的译文。占位符统一用 {0} {1} 这种 .NET 风格。</summary>
    public static string T(string format, params object[] args) => string.Format(System.Globalization.CultureInfo.InvariantCulture, T(format), args);

    public static string LanguageLabel(AppLanguage language) => language switch
    {
        AppLanguage.Auto => "跟随系统",
        AppLanguage.Zh => "简体中文",
        _ => "English"
    };
}

/// <summary>中文 → 英文 的映射表。键是代码里写的中文原文。</summary>
internal static class English
{
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        // 通用
        ["刷新"] = "Refresh",
        ["刷新中…"] = "Refreshing…",
        ["设置"] = "Settings",
        ["退出"] = "Quit",
        ["取消"] = "Cancel",
        ["关闭"] = "Close",
        ["重试"] = "Retry",
        ["确定"] = "OK",
        ["复制分支名"] = "Copy Branch",
        ["复制提交 SHA"] = "Copy Commit SHA",
        ["搜索"] = "Search",
        ["加载中…"] = "Loading…",
        ["运行中"] = "Running",
        ["全部"] = "All",
        ["失败"] = "Failed",
        ["成功"] = "Passed",
        ["已取消"] = "Canceled",
        ["其他"] = "Other",
        ["未知"] = "Unknown",
        ["未配置"] = "Not configured",
        ["星标"] = "Starred",
        ["项目"] = "Projects",
        ["流水线"] = "Pipelines",
        ["分支"] = "Branch",
        ["提交"] = "Commit",
        ["作者"] = "Author",
        ["耗时"] = "Duration",
        ["覆盖率"] = "Coverage",
        ["阶段"] = "Stage",
        ["作业"] = "Jobs",
        ["详情"] = "Details",
        ["打开 GitLab"] = "Open GitLab",
        ["拖动底边调整高度，双击恢复默认"] = "Drag the bottom edge to resize · double-click to reset",
        ["{0} {1}"] = "{0} {1}",
        // 更新
        ["检查更新"] = "Check for Updates",
        ["自动检查更新"] = "Check for updates automatically",
        ["开启后每天自动检查一次，发现新版本会弹窗展示更新内容，由你决定立即更新还是忽略此版本。"] = "Checks once a day. When a new version is found, a window shows the release notes — update now or skip this version.",
        ["已忽略 {0} 的更新提示"] = "Update prompt skipped for {0}",
        ["取消忽略"] = "Undo Skip",
        ["忽略此版本"] = "Skip This Version",
        ["立即更新"] = "Update Now",
        ["这个版本没有写更新说明。"] = "This release has no notes.",
        ["正在检查更新…"] = "Checking for Updates…",
        ["更新到 {0}"] = "Update to {0}",
        ["更新"] = "Updates",
        ["有新版本 {0}"] = "New version {0}",
        ["当前版本 {0}"] = "Current version {0}",
        ["已是最新版本"] = "You're up to date",
        ["检查更新失败"] = "Could not check for updates",
        ["正在下载更新… {0}%"] = "Downloading update… {0}%",
        ["正在安装更新…"] = "Installing update…",
        ["即将退出并安装更新，应用会自动重新启动。"] = "The app will quit to install the update, then reopen automatically.",
        ["更新源：GitHub Release"] = "Update source: GitHub Release",
        ["下载地址无效"] = "Invalid download URL",
        ["无法连接 GitHub（可能被网络或代理挡住）"] = "Cannot reach GitHub (your network or proxy may be blocking it)",
        ["GitHub 接口访问过于频繁，稍后再试"] = "GitHub rate limit reached — try again later",
        ["下载校验失败，已放弃更新"] = "Download verification failed; update aborted",
        ["压缩包里没找到程序，已放弃更新"] = "No executable found in the archive; update aborted",

        // 面板
        ["未配置 GitLab 连接"] = "GitLab is not configured",
        ["填写服务器地址和访问令牌后，这里会显示你打星项目的流水线。"] = "Add your server address and access token to see pipelines of your starred projects.",
        ["开始配置"] = "Start setup",
        ["没有可监控的项目"] = "No projects to monitor",
        ["在设置里勾选要监控的项目，或开启「仅监控星标项目」。"] = "Pick projects in Settings, or turn on “Starred projects only”.",
        ["所有流水线都通过了"] = "All pipelines are passing",
        ["没有符合筛选条件的流水线"] = "No pipelines match the filter",
        ["连接失败"] = "Connection failed",
        ["重新加载"] = "Reload",
        ["暂无流水线记录"] = "No pipeline records yet",
        ["更新于 {0}"] = "Updated {0}",
        ["共 {0} 个项目 · {1} 条流水线"] = "{0} projects · {1} pipelines",
        ["显示 {0} / {1} 条流水线"] = "Showing {0} of {1} pipelines",
        ["运行新流水线"] = "Run pipeline",
        ["在项目页打开"] = "Open in GitLab",
        ["该项目的流水线加载失败"] = "Failed to load pipelines for this project",

        // 流水线状态
        ["已创建"] = "Created",
        ["等待资源"] = "Waiting for resource",
        ["准备中"] = "Preparing",
        ["排队中"] = "Pending",
        ["已通过"] = "Passed",
        ["已失败"] = "Failed",
        ["已跳过"] = "Skipped",
        ["手动"] = "Manual",
        ["已计划"] = "Scheduled",
        ["已阻塞"] = "Blocked",
        ["未开始"] = "Not started",

        // 详情
        ["流水线详情"] = "Pipeline details",
        ["提交信息"] = "Commit message",
        ["触发方式"] = "Triggered by",
        ["开始时间"] = "Started",
        ["结束时间"] = "Finished",
        ["名称"] = "Name",
        ["创建时间"] = "Created",
        ["更新时间"] = "Updated",
        ["重跑流水线"] = "Retry pipeline",
        ["停止流水线"] = "Cancel pipeline",
        ["打开流水线页面"] = "Open pipeline page",
        ["查看作业日志"] = "View job log",
        ["重跑作业"] = "Retry job",
        ["停止作业"] = "Cancel job",
        ["允许失败"] = "Allowed to fail",
        ["无作业"] = "No jobs",
        ["作业加载失败"] = "Failed to load jobs",

        // 运行流水线
        ["选择分支或标签"] = "Choose a branch or tag",
        ["运行"] = "Run",
        ["默认分支"] = "Default branch",
        ["没有可用的分支"] = "No branches available",
        ["流水线已创建"] = "Pipeline created",
        // 设置 - 连接
        ["连接"] = "Connection",
        ["服务器地址"] = "Server address",
        ["例如 https://gitlab.example.com"] = "e.g. https://gitlab.example.com",
        ["令牌类型"] = "Token type",
        ["个人访问令牌 (PRIVATE-TOKEN)"] = "Personal access token (PRIVATE-TOKEN)",
        ["OAuth 令牌 (Bearer)"] = "OAuth token (Bearer)",
        ["访问令牌"] = "Access token",
        ["粘贴令牌"] = "Paste your token",
        ["令牌在本机加密保存（AES-GCM，密钥来自这台电脑和你的登录账户），不会写进配置文件。"] = "The token is encrypted on this PC (AES-GCM, key derived from this machine and your account), never stored in a config file.",
        ["测试连接"] = "Test connection",
        ["正在测试…"] = "Testing…",
        ["连接成功"] = "Connected",
        ["令牌需要 read_api 权限；若还要读取未加入的项目，请加上 read_user。"] = "The token needs the read_api scope; add read_user to see projects you have not joined.",
        ["创建令牌"] = "Create a token",
        ["服务器"] = "Server",
        ["GitLab 版本"] = "GitLab version",
        ["登录用户"] = "Signed in as",
        ["令牌账户"] = "Token account",
        ["已配置"] = "Configured",

        // 设置 - 项目
        ["仅监控星标项目"] = "Starred projects only",
        ["手动选择要监控的项目"] = "Choose projects manually",
        ["全选"] = "Select all",
        ["清空"] = "Clear",
        ["每个项目显示的流水线数"] = "Pipelines per project",
        ["重新拉取项目列表"] = "Reload project list",
        ["开启后只监控你打过星的项目；关闭后可在下方手动勾选。"] = "When on, only starred projects are monitored. When off, pick them below.",
        ["没有星标项目"] = "No starred projects",
        ["先在上面填好地址和令牌，然后重新拉取项目列表。"] = "Fill in the address and token above, then reload the project list.",
        ["已加载 {0} 个星标项目、{1} 个参与项目"] = "{0} starred and {1} member projects loaded",
        ["已选择 {0} 个项目"] = "{0} projects selected",

        // 设置 - 通用
        ["通用"] = "General",
        ["语言"] = "Language",
        ["从不"] = "Never",
        ["打开面板时自动刷新"] = "Refresh when opening the panel",
        ["托盘图标显示状态"] = "Show status on the tray icon",
        ["显示失败数量"] = "Show failing count",
        ["流水线失败时通知"] = "Notify when a pipeline fails",
        ["流水线恢复时通知"] = "Notify when a pipeline recovers",
        ["登录时启动"] = "Launch at sign-in",
        ["自动刷新"] = "Auto refresh",
        ["每 {0} 秒"] = "Every {0} seconds",
        ["每分钟"] = "Every minute",
        ["每 2 分钟"] = "Every 2 minutes",
        ["每 5 分钟"] = "Every 5 minutes",
        ["每 10 分钟"] = "Every 10 minutes",
        ["面板宽度"] = "Panel width",
        ["托盘图标"] = "Tray icon",
        ["忽略版本"] = "Ignored version",
        ["面板高度"] = "Panel height",
        ["通知已在系统设置中关闭，Windows 一般不会拦截托盘气泡，但专注 assist 状态可能不显示。"] = "Windows rarely blocks tray balloons, but they stay hidden while do-not-disturb is on.",

        ["版本 {0} ({1})"] = "Version {0} ({1})",

        // 设置 - 关于
        ["关于"] = "About",
        ["原生 WPF + Win32 托盘应用，无 Electron、无外部依赖。"] = "Native WPF + Win32 tray app. No Electron, no dependencies.",
        ["复制诊断信息"] = "Copy diagnostics",
        ["在资源管理器中显示"] = "Show in Explorer",
        ["项目主页"] = "Project home",

        // 通知
        ["流水线失败"] = "Pipeline failed",
        ["流水线已恢复"] = "Pipeline recovered",

        // 错误
        ["未配置服务器地址或令牌"] = "Server address or token is not configured",
        ["服务器地址无效，请填写形如 https://gitlab.example.com 的地址"] = "Invalid server address. Use a form like https://gitlab.example.com",
        ["服务器返回的是网页而不是 API，请检查地址是否指向 GitLab 根地址"] = "The server returned a web page instead of API data. Check that the address points at your GitLab root URL",
        ["无法连接服务器，请检查地址、网络或 VPN"] = "Cannot reach the server. Check the address, network or VPN",
        ["令牌无效或已过期（401）"] = "Token is invalid or expired (401)",
        ["没有权限（403）"] = "Permission denied (403)",
        ["项目不存在或已被删除（404）"] = "Project not found or deleted (404)",
        ["请求过于频繁（429），请加大刷新间隔"] = "Too many requests (429). Increase the refresh interval",
        ["数据解析失败，可能不是 GitLab 服务器"] = "Failed to parse the response — this may not be a GitLab server",
        ["请求已取消"] = "Request canceled",

        // 打星与项目列表
        ["打星"] = "Star",
        ["取消打星"] = "Unstar",
        ["监控中"] = "Monitored",
        ["参与"] = "Member",
        ["正在加载参与项目…"] = "Loading member projects…",
        ["还没有项目"] = "No projects yet",
        ["先在「连接」里填好地址和令牌，再点「重新拉取项目列表」；列表出来后点行首的星标就能打星。"] = "Fill in the address and token under Connection, then reload the project list. Click the star at the start of a row to star a project.",
        ["每 {0} 条"] = "{0} per project",
        ["地址"] = "Address",
        ["隐藏"] = "Hide",
        ["显示"] = "Show",
        ["改动即时生效，无需保存。"] = "Changes apply immediately — no save needed.",

        // 托盘菜单
        ["暂停自动刷新"] = "Pause Auto Refresh",
        ["恢复自动刷新"] = "Resume Auto Refresh",
        ["显示面板"] = "Show Panel",

        // 杂项
        ["项目 / 分支"] = "project / branch",
        ["试试切换筛选或清空搜索。"] = "Try another filter or clear the search.",
        ["归档"] = "Archived",
        ["默认"] = "default",
        ["服务器错误"] = "Server error",
        ["秒前"] = "seconds ago",
        ["分钟前"] = "minutes ago",
        ["小时前"] = "hours ago",
        ["天前"] = "days ago",
        ["周前"] = "weeks ago",
        ["个月前"] = "months ago",
        ["年前"] = "years ago",
        ["昨天"] = "yesterday"
    };
}
