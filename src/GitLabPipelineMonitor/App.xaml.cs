using System.Windows;
using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.Localization;
using GitLabPipelineMonitor.Core.Storage;
using GitLabPipelineMonitor.Views;

namespace GitLabPipelineMonitor;

/// <summary>
/// 应用入口。负责按顺序把宿主、设置、状态、托盘、更新器串起来，
/// 并处理命令行诊断参数（--demo / --updateprobe 等，与 macOS 版对齐）。
/// </summary>
public partial class App : Application
{
    private WindowsHost? _host;
    private AppSettings? _settings;
    private Core.AppState? _state;
    private TrayIcon? _tray;
    private PanelWindow? _panel;
    private FloatingBadgeWindow? _badge;
    private SettingsWindow? _settingsWindow;
    private UpdateManager? _updater;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 更新助手模式：这是替换自己用的临时实例。
        // 必须在单实例互斥体之前处理 —— 否则助手会被自己的单实例检查挡掉。
        if (UpdateHelper.TryRun(e.Args))
        {
            Shutdown();
            return;
        }

        // 越早装越好：连启动阶段的错误也要能留下现场，而不是弹系统崩溃框
        CrashGuard.Install(new WindowsHost());

        // 启动过程整体兜住。托盘程序一旦半初始化（起了进程却没图标）是最难查的状态，
        // 所以这里失败就弹窗说清楚并退出，绝不静默继续。
        try
        {
            StartCore(e);
        }
        catch (Exception error)
        {
            CrashGuard.Write("启动", error, fatal: true);
            CrashGuard.ShowDialog(error);
            Shutdown();
        }
    }

    private void StartCore(StartupEventArgs e)
    {
        // 先上色再建窗口：面板、设置、菜单都引用同一批画刷对象，这里定完深浅后面不用再管
        ThemeManager.Install();

        // 单实例：已经在跑就把已有面板叫到前台，不要开第二份托盘图标
        _instanceMutex = new System.Threading.Mutex(true, "GitLabPipelineMonitor_SingleInstance", out var createdNew);
        // createdNew=false 时只是「打开」了同名互斥体，并没有拿到所有权。
        // 这种情况下退出时不能 ReleaseMutex，否则抛
        // ApplicationException: Object synchronization method was called from an unsynchronized block of code.
        _ownsInstanceMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show(Loc.T("GitLab Pipeline Monitor 已经在运行了（看屏幕右下角的系统托盘）。"),
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _host = new WindowsHost();
        AppInfo.Version = GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        AppInfo.Build = "12";

        _settings = new AppSettings(_host);
        Loc.Language = _settings.Language;

        _state = new Core.AppState(_settings, _host, new Core.GitLab.HttpTransport());
        _updater = new UpdateManager(_settings, _host);

        _panel = new PanelWindow(_state, _settings, _updater);
        _tray = new TrayIcon(_state, _settings, _updater, _panel, _host);
        // 托盘建起来了：之后的异常可以用气泡通知告诉用户
        CrashGuard.TrayReady = true;

        // 屏幕边缘浮标：托盘图标被收进「隐藏的图标」时，靠它看有没有流水线在跑。
        // 数据跟托盘图标共用同一份状态，不额外发请求。
        _badge = new FloatingBadgeWindow(_state, _settings, _panel, _tray);
        _state.IconNeedsUpdate += OnIconNeedsUpdate;
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Core.AppState.Pipelines)) _badge?.Rebuild();
        };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.FloatingBadgeEnabled)) ApplyBadgeVisibility();
        };

        // 诊断模式：抓完屏/跑完探针就退，不进托盘
        if (Diagnostics.RunIfRequested(e.Args, _state, _settings, _updater, _panel, _tray, _badge))
        {
            Shutdown();
            return;
        }

        _state.Bootstrap();
        _updater.Start();
        ApplyBadgeVisibility();

        // 记一行启动现场：主题判定、强调色、托盘图标来源。
        // 以后出怪问题，把 error.log 发过来就能看出是哪一步不对。
        CrashGuard.Log("启动完成",
            $"版本 {AppInfo.Version} · 深浅 {(ThemeManager.IsDark ? "深" : "浅")}"
            + $" · 任务栏 {(ThemeManager.TaskbarIsLight ? "浅" : "深")}"
            + $" · 强调色 {ThemeManager.Accent}"
            + $" · 托盘图标 {_tray.IconDescription}");
    }

    private System.Threading.Mutex? _instanceMutex;

    /// <summary>这份实例是否真的持有了单实例互斥体。只有持有者才能 ReleaseMutex。</summary>
    private bool _ownsInstanceMutex;

    /// <summary>面板要拿宿主能力（打开链接、复制剪贴板）。</summary>
    public WindowsHost GetHost() => _host!;

    /// <summary>状态刷新完了：浮标跟着重建（托盘图标也是这时候重画的）。</summary>
    private void OnIconNeedsUpdate() => _badge?.Rebuild();

    /// <summary>按设置里的开关显示/隐藏浮标。</summary>
    private void ApplyBadgeVisibility()
    {
        if (_badge is null) return;
        if (_settings?.FloatingBadgeEnabled == true)
        {
            if (!_badge.IsVisible) _badge.Show();
            _badge.Reposition();
        }
        else if (_badge.IsVisible)
        {
            _badge.Hide();
        }
    }

    /// <summary>供托盘打开设置窗口（同一个实例只开一个）。</summary>
    public void ShowSettings(SettingsTab tab = SettingsTab.Connection)
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            _settingsWindow.SelectTab(tab);
            return;
        }
        _settingsWindow = new SettingsWindow(_state!, _settings!, _updater!, _host!, tab);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _badge?.Close();
        _state?.Dispose();
        _updater?.Dispose();
        // 第二份实例没拿到互斥体的所有权，释放它会抛异常 —— 只在持有时释放
        if (_ownsInstanceMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* 不是本线程拿到的，忽略 */ }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}