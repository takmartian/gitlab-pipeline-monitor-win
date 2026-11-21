using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Localization;
using GitLabPipelineMonitor.Core.Storage;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Label = System.Windows.Controls.Label;
using GroupBox = System.Windows.Controls.GroupBox;
using Brush = System.Windows.Media.Brush;

namespace GitLabPipelineMonitor.Views;

/// <summary>
/// 设置窗口：连接 / 项目 / 通用 / 关于，条目与 macOS 版 SettingsView 逐条对齐。
/// AppSettings 的 setter 即时生效并自动落盘，所以这里没有「保存」按钮。
/// 不引 MVVM 框架：DataContext 就是窗口本身，绑定走 Settings.X 路径，
/// 动态文案（计数、状态、更新进度）由 RefreshDynamic 统一刷新。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppState _state;
    private readonly AppSettings _settings;
    private readonly UpdateManager _updater;
    private readonly WindowsHost _host;

    /// <summary>InitializeComponent 期间 TabControl 就会抛 SelectionChanged，
    /// 那时命名字段还没建全，事件处理要先返回。</summary>
    private bool _ready;

    private bool _testing;
    private bool _reloading;
    private bool? _testOk;
    private string? _testError;
    private AppState.TestResult? _testInfo;

    private bool _revealToken;
    private bool _syncingToken;
    private bool _syncingLaunch;
    private string _query = "";

    /// <summary>诊断抓图时的替身服务器地址；null = 显示真实地址。</summary>
    private string? _previewServerUrl;

    private readonly List<SettingsOption> _authOptions = new();
    private readonly List<SettingsOption> _languageOptions = new();
    private readonly List<SettingsOption> _refreshOptions = new();
    private readonly List<SettingsOption> _widthOptions = new();

    /// <summary>项目页要显示的行（已按搜索词过滤）。</summary>
    public ObservableCollection<SettingsProjectRow> ProjectRows { get; } = new();

    // 绑定入口：DataContext = this，XAML 里用 Settings.ServerUrl 这种路径
    public AppState State => _state;
    public AppSettings Settings => _settings;
    public UpdateManager Updater => _updater;
    public WindowsHost Host => _host;

    public SettingsWindow(AppState state, AppSettings settings, UpdateManager updater, WindowsHost host, SettingsTab tab)
    {
        _state = state;
        _settings = settings;
        _updater = updater;
        _host = host;

        InitializeComponent();
        DataContext = this;

        // 深色系统下标题栏要跟着变深色，否则浅色标题栏配深色内容很扎眼
        ThemeManager.AttachDarkTitleBar(this);

        Title = Loc.T("设置");
        AboutNameText.Text = AppInfo.Name;
        BuildOptions();

        // PasswordBox 的 Password 不是依赖属性、不能绑定，手工灌初值
        _syncingToken = true;
        TokenHidden.Password = settings.Token;
        _syncingToken = false;
        _syncingLaunch = true;
        LaunchBox.IsChecked = host.LaunchAtLogin;
        _syncingLaunch = false;

        SelectTab(tab);

        _state.PropertyChanged += State_PropertyChanged;
        _settings.PropertyChanged += Settings_PropertyChanged;
        _updater.PropertyChanged += Updater_PropertyChanged;
        Closed += (_, _) =>
        {
            _state.PropertyChanged -= State_PropertyChanged;
            _settings.PropertyChanged -= Settings_PropertyChanged;
            _updater.PropertyChanged -= Updater_PropertyChanged;
        };

        _ready = true;
        RefreshAll();

        // 打开这一页就要有参与项目可选：平时「仅监控星标」不会去拉它
        _state.EnsureMemberProjects();
        Loaded += (_, _) => _state.EnsureMemberProjects();
    }

    // MARK: - 标签切换

    /// <summary>窗口已经开着时再次调用：切到指定标签（托盘菜单「设置」用）。</summary>
    public void SelectTab(SettingsTab tab) => Tabs.SelectedIndex = (int)tab;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 页面里的 ComboBox 也抛 SelectionChanged（它会冒泡上来），只认 TabControl 自己那一次
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (!_ready) return;
        if (Tabs.SelectedIndex == (int)SettingsTab.Projects) _state.EnsureMemberProjects();
    }

    // MARK: - 下拉选项

    private void BuildOptions()
    {
        _authOptions.Clear();
        _authOptions.Add(new SettingsOption(GitLabAuthMode.Pat, () => GitLabAuthMode.Pat.Label()));
        _authOptions.Add(new SettingsOption(GitLabAuthMode.Oauth, () => GitLabAuthMode.Oauth.Label()));
        Fill(AuthModeBox, _authOptions);

        _languageOptions.Clear();
        _languageOptions.Add(new SettingsOption(AppLanguage.Auto, () => Loc.LanguageLabel(AppLanguage.Auto)));
        _languageOptions.Add(new SettingsOption(AppLanguage.Zh, () => Loc.LanguageLabel(AppLanguage.Zh)));
        _languageOptions.Add(new SettingsOption(AppLanguage.En, () => Loc.LanguageLabel(AppLanguage.En)));
        Fill(LanguageBox, _languageOptions);

        _refreshOptions.Clear();
        foreach (var seconds in AppSettings.RefreshIntervals)
            _refreshOptions.Add(new SettingsOption(seconds, () => AppSettings.RefreshIntervalLabel(seconds)));
        Fill(RefreshBox, _refreshOptions);

        _widthOptions.Clear();
        foreach (var width in AppSettings.PanelWidths)
            _widthOptions.Add(new SettingsOption(width, () => string.Format(Loc.T("{0} px"), width)));
        Fill(WidthBox, _widthOptions);
    }

    private static void Fill(ComboBox box, List<SettingsOption> options)
    {
        var selected = box.SelectedValue;
        box.Items.Clear();
        foreach (var option in options) box.Items.Add(option);
        // SetCurrentValue：把选中项对回值，同时不顶掉 XAML 里的 SelectedValue 双向绑定
        if (selected is not null)
            box.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, selected);
    }

    /// <summary>展开第一个下拉框（诊断抓图用：看下拉项本身的显示对不对）。</summary>
    public void OpenFirstDropDown() => LanguageBox.IsDropDownOpen = true;

    /// <summary>语言下拉框的弹出层（诊断抓图用：要拿到它的窗口句柄才能单独抓那张弹层）。</summary>
    public System.Windows.Controls.Primitives.Popup? LanguagePopup()
        => LanguageBox.Template?.FindName("PART_Popup", LanguageBox) as System.Windows.Controls.Primitives.Popup;

    /// <summary>
    /// 诊断抓图用：把界面上的服务器地址换成演示地址，别把真实地址拍进截图。
    ///
    /// 只换「显示」不碰设置本体 —— 改 settings.ServerUrl 会立刻落盘，
    /// 把用户自己的服务器地址写坏。所以这里换成另一个绑定源。
    /// </summary>
    public void UsePreviewServerUrl(string preview = "https://gitlab.example.com")
    {
        _previewServerUrl = preview;
        ServerBox.SetBinding(System.Windows.Controls.TextBox.TextProperty, new System.Windows.Data.Binding("ServerUrl")
        {
            Source = new PreviewServerSource(preview),
            Mode = System.Windows.Data.BindingMode.TwoWay,
            UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
        });
        UpdatePlaceholders();
        RefreshDynamic();
    }

    /// <summary>预览用的服务器地址源：只读，写入一律丢弃（预览模式下不该改用户配置）。</summary>
    private sealed class PreviewServerSource
    {
        private readonly string _url;
        public PreviewServerSource(string url) => _url = url;
        public string ServerUrl
        {
            get => _url;
            set { /* 预览模式不接受写入 */ }
        }
    }

    /// <summary>下拉框状态的自查信息（诊断用：项数 / 选中项 / 选中值）。</summary>
    public string DebugDump()
    {
        var parts = new List<string>();
        foreach (var pair in new[]
                 {
                     ("令牌类型", AuthModeBox), ("语言", LanguageBox),
                     ("自动刷新", RefreshBox), ("面板宽度", WidthBox),
                 })
        {
            var box = pair.Item2;
            parts.Add($"{pair.Item1}: 项数={box.Items.Count} 选中项={box.SelectedItem ?? "无"} 选中值={box.SelectedValue ?? "无"}");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>语言切换后重算下拉项文字（选项对象复用，绑定不中断）。</summary>
    private void RefreshOptionTexts()
    {
        foreach (var group in new[] { _authOptions, _languageOptions, _refreshOptions, _widthOptions })
            foreach (var option in group) option.RefreshText();
    }

    // MARK: - 令牌显示 / 隐藏

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        _revealToken = !_revealToken;
        if (!_revealToken)
        {
            _syncingToken = true;
            TokenHidden.Password = _settings.Token;
            _syncingToken = false;
        }
        RefreshDynamic();
        if (_revealToken) TokenBox.Focus(); else TokenHidden.Focus();
    }

    private void TokenHidden_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingToken) return;
        _settings.Token = TokenHidden.Password;
    }

    /// <summary>从别处（明文框、切换服务器读回令牌）改的值要同步到密码框。</summary>
    private void SyncHiddenToken()
    {
        if (TokenHidden.Password == _settings.Token) return;
        _syncingToken = true;
        TokenHidden.Password = _settings.Token;
        _syncingToken = false;
    }

    // MARK: - 连接

    private void CreateToken_Click(object sender, RoutedEventArgs e)
    {
        var base_ = _settings.NormalizedBaseUrl ?? "https://gitlab.com";
        _host.OpenUrl(base_.TrimEnd('/') + "/-/user_settings/personal_access_tokens");
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_testing) return;
        _testing = true;
        _testOk = null;
        _testInfo = null;
        _testError = null;
        RefreshDynamic();

        var (result, error) = await _state.TestConnectionAsync();

        _testing = false;
        if (result is not null)
        {
            _testOk = true;
            _testInfo = result;
            // 连上之后立刻把数据拉起来，并把参与项目带上，项目页马上能选
            _state.Refresh(force: true);
            _state.EnsureMemberProjects();
        }
        else
        {
            _testOk = false;
            _testError = error;
        }
        RefreshDynamic();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_reloading) return;
        _reloading = true;
        _state.LoadProjects(force: true);
        // 拉取是异步的且不一定更新 IsLoadingProjects，给个短延时让按钮回到可用（与 macOS 版一致）
        await Task.Delay(1200);
        _reloading = false;
        RefreshDynamic();
    }

    private void Unignore_Click(object sender, RoutedEventArgs e) => _settings.IgnoredUpdateVersion = null;

    private void Launch_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingLaunch) return;
        _host.LaunchAtLogin = LaunchBox.IsChecked == true;
    }

    /// <summary>占位文字显隐：输入框有内容就藏掉提示。
    /// 单独抽出来是因为输入框的值可能来自绑定 —— 构造函数里绑定还没把已保存的
    /// 地址灌进来，只在 RefreshDynamic 里算一次的话占位提示会一直亮着，
    /// 和真实值叠成两行字。绑定推值时会触发 TextChanged，所以两边都靠它。</summary>
    private void UpdatePlaceholders()
    {
        if (ServerPlaceholder != null)
            ServerPlaceholder.Visibility = string.IsNullOrEmpty(ServerBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (TokenPlaceholder != null)
            TokenPlaceholder.Visibility = string.IsNullOrEmpty(_settings.Token) ? Visibility.Visible : Visibility.Collapsed;
        // 搜索框的占位文字不用管：它在 SearchBox 样式模板里，
        // 靠 Text 为空的触发器显隐，跟着正文走，不用在这里手动切。
    }

    private void ServerBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePlaceholders();

    // MARK: - 项目页

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholders();
        if (!_ready) return;
        _query = SearchBox.Text?.Trim() ?? "";
        RebuildRows();
    }

    private void RebuildRows()
    {
        var needle = _query.ToLowerInvariant();
        var list = _state.AvailableProjects;
        ProjectRows.Clear();
        foreach (var project in list)
        {
            if (needle.Length > 0 &&
                project.PathWithNamespace.ToLowerInvariant().Contains(needle, StringComparison.Ordinal) == false &&
                project.Name.ToLowerInvariant().Contains(needle, StringComparison.Ordinal) == false)
                continue;
            var row = new SettingsProjectRow(this, project);
            row.SetBusy(_state.BusyAction);
            ProjectRows.Add(row);
        }
        RefreshDynamic();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
        => _settings.SetMonitoredProjectIds(ProjectRows.Select(row => row.Project.Id));

    private void Clear_Click(object sender, RoutedEventArgs e) => _settings.SetMonitoredProjectIds(Array.Empty<int>());

    private void StarRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SettingsProjectRow row }) return;
        _state.SetStarred(row.Project, !row.Project.Starred);
    }

    private void OpenRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SettingsProjectRow row }) return;
        var base_ = _settings.NormalizedBaseUrl;
        var url = ServerAddress.SafeBrowserUrl(row.Project.ResolvedWebUrl(base_), base_);
        if (url is not null) _host.OpenUrl(url);
    }

    // MARK: - 关于

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        await _updater.CheckAsync(force: true);
        RefreshDynamic();
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e) => _updater.PresentUpdateNotice();

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e) => _host.CopyToClipboard(BuildDiagnostics());

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exe}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 非 Windows 或资源管理器打不开：静默失败，不打断设置窗口
        }
    }

    private void ProjectHome_Click(object sender, RoutedEventArgs e)
        => _host.OpenUrl("https://github.com/" + AppInfo.Repo);

    private string BuildDiagnostics()
    {
        var configured = _settings.Token.Trim().Length > 0;
        return string.Join("\n", new[]
        {
            $"{AppInfo.Name} {AppInfo.Version} ({AppInfo.Build})",
            "OS: " + RuntimeInformation.OSDescription,
            "Path: " + (Environment.ProcessPath ?? "—"),
            Loc.T("服务器") + ": " + Fmt.Host(_settings.NormalizedBaseUrl),
            Loc.T("令牌账户") + ": " + _settings.TokenAccount,
            Loc.T("访问令牌") + ": " + (configured ? Loc.T("已配置") : Loc.T("未配置")),
            Loc.T("语言") + ": " + Loc.LanguageLabel(_settings.Language),
            Loc.T("自动刷新") + ": " + AppSettings.RefreshIntervalLabel(_settings.RefreshInterval),
            Loc.T("项目") + ": " + _state.MonitoredProjects.Count,
            Loc.T("流水线") + ": " + _state.AllVisiblePipelines.Count,
            Loc.T("每个项目显示的流水线数") + ": " + _settings.PipelinesPerProject,
            Loc.T("忽略版本") + ": " + (_settings.IgnoredUpdateVersion ?? "—")
        });
    }

    // MARK: - 事件 → 刷新

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_ready) return;
        // Core 的状态是在后台线程上改的，PropertyChanged 也从那儿抛过来。
        // 这里动的是 ProjectRows（ObservableCollection）和输入框，必须回 UI 线程。
        UiThread.Run(() => HandleStateChanged(e.PropertyName));
    }

    private void HandleStateChanged(string? name)
    {
        if (!_ready) return;
        switch (name)
        {
            case nameof(AppState.StarredProjects):
            case nameof(AppState.MemberProjects):
            case nameof(AppState.AvailableProjects):
            case nameof(AppState.MonitoredProjects):
                RebuildRows();
                return;
            case nameof(AppState.BusyAction):
                foreach (var row in ProjectRows) row.SetBusy(_state.BusyAction);
                break;
        }
        RefreshDynamic();
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_ready) return;
        // 设置也可能被后台线程改（比如首次拉取后自动带上星标项目），同样要回 UI 线程
        UiThread.Run(() => HandleSettingsChanged(e.PropertyName));
    }

    private void HandleSettingsChanged(string? name)
    {
        if (!_ready) return;
        switch (name)
        {
            case nameof(AppSettings.MonitoredProjectIds):
                foreach (var row in ProjectRows) row.RefreshMonitored();
                break;
            case nameof(AppSettings.OnlyStarred):
                foreach (var row in ProjectRows) row.RefreshFlags();
                break;
            case nameof(AppSettings.Token):
                SyncHiddenToken();
                break;
            case null:
                // 语言切换：静态文案和下拉项文字都要重算
                LocalizedText.ApplyTree(this);
                RefreshOptionTexts();
                Title = Loc.T("设置");
                break;
        }
        RefreshDynamic();
    }

    private void Updater_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        => UiThread.Run(RefreshDynamic);

    // MARK: - 动态文案

    private void RefreshAll()
    {
        LocalizedText.ApplyTree(this);
        RefreshOptionTexts();
        RebuildRows();
        RefreshDynamic();
    }

    /// <summary>把所有随状态变化的文字 / 可见性集中刷一遍。改动即时生效，所以不需要提交动作。</summary>
    private void RefreshDynamic()
    {
        if (!_ready) return;

        // 占位文字：有值就藏起来（WPF 原生 TextBox/PasswordBox 没有占位符）
        UpdatePlaceholders();

        // 令牌显示 / 隐藏
        TokenBox.Visibility = _revealToken ? Visibility.Visible : Visibility.Collapsed;
        TokenHidden.Visibility = _revealToken ? Visibility.Collapsed : Visibility.Visible;
        RevealButton.Content = Loc.T(_revealToken ? "隐藏" : "显示");
        RevealButton.ToolTip = Loc.T(_revealToken ? "隐藏" : "显示");
        CreateTokenRun.Text = Loc.T("创建令牌");

        // 测试连接
        TestButton.Content = _testing ? Loc.T("正在测试…") : Loc.T("测试连接");
        TestButton.IsEnabled = !_testing && _settings.IsConfigured;
        ReloadButton.Content = Loc.T("重新拉取项目列表");
        ReloadButton.IsEnabled = !_reloading && _settings.IsConfigured;

        if (_testOk is null)
        {
            TestResultRow.Visibility = Visibility.Collapsed;
            TestInfoPanel.Visibility = Visibility.Collapsed;
            TestErrorText.Visibility = Visibility.Collapsed;
        }
        else if (_testOk == true && _testInfo is { } info)
        {
            TestResultRow.Visibility = Visibility.Visible;
            TestStatusIcon.Data = GeometryFrom("Icon.CheckCircle");
            TestStatusIcon.Fill = BrushFor("SuccessBrush");
            TestStatusText.Text = Loc.T("连接成功");
            TestStatusText.Foreground = BrushFor("SuccessBrush");
            TestErrorText.Visibility = Visibility.Collapsed;
            TestInfoPanel.Visibility = Visibility.Visible;
            InfoServerValue.Text = Fmt.Host(_settings.NormalizedBaseUrl);
            var user = info.User;
            InfoUserValue.Text = user is null ? "—" : $"{user.Name} · @{user.Username}";
            InfoVersionValue.Text = string.IsNullOrEmpty(info.Version) ? "—" : info.Version;
            InfoProjectsValue.Text = Loc.T("已加载 {0} 个星标项目、{1} 个参与项目", info.StarredCount, info.MemberCount);
        }
        else
        {
            TestResultRow.Visibility = Visibility.Visible;
            TestInfoPanel.Visibility = Visibility.Collapsed;
            TestStatusIcon.Data = GeometryFrom("Icon.CloseCircle");
            TestStatusIcon.Fill = BrushFor("FailedBrush");
            TestStatusText.Text = Loc.T("连接失败");
            TestStatusText.Foreground = BrushFor("FailedBrush");
            var message = _testError ?? "";
            TestErrorText.Text = message;
            TestErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        // 项目页计数
        var starred = _state.StarredProjects.Count;
        var member = _state.MemberProjects.Count;
        var monitored = _settings.OnlyStarred ? starred : _settings.MonitoredProjectIds.Count;
        StarCountText.Text = Loc.T("星标") + " " + starred;
        MemberCountText.Text = Loc.T("参与") + " " + member;
        MonitoredCountText.Text = Loc.T("监控中") + " " + monitored;
        LoadingMembersText.Text = Loc.T("正在加载参与项目…");
        LoadingMembersText.Visibility = _state.IsLoadingProjects && member == 0 ? Visibility.Visible : Visibility.Collapsed;
        PickButtons.Visibility = _settings.OnlyStarred ? Visibility.Collapsed : Visibility.Visible;
        SelectAllButton.Content = Loc.T("全选");
        ClearButton.Content = Loc.T("清空");
        SelectedCountText.Text = Loc.T("已选择 {0} 个项目", monitored);
        SelectedCountText.Visibility = _settings.OnlyStarred ? Visibility.Collapsed : Visibility.Visible;
        LoadedSummaryText.Text = Loc.T("已加载 {0} 个星标项目、{1} 个参与项目", starred, member);
        LoadedSummaryText.Visibility = starred + member == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProjectList.Visibility = ProjectRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProjectEmptyState.Visibility = ProjectRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PipelineCountText.Text = Loc.T("每 {0} 条", _settings.PipelinesPerProject);

        // 通用：更新分组、通知可用性、开机自启
        var ignored = _settings.IgnoredUpdateVersion;
        IgnoredUpdateRow.Visibility = string.IsNullOrEmpty(ignored) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(ignored)) IgnoredUpdateText.Text = Loc.T("已忽略 {0} 的更新提示", ignored);
        NotifyHintRow.Visibility = _host.NotificationsAvailable ? Visibility.Collapsed : Visibility.Visible;
        if (!_syncingLaunch)
        {
            _syncingLaunch = true;
            LaunchBox.IsChecked = _host.LaunchAtLogin;
            _syncingLaunch = false;
        }

        // 关于
        AboutVersionText.Text = Loc.T("版本 {0} ({1})", AppInfo.Version, AppInfo.Build);
        AboutServerValue.Text = _previewServerUrl is null
            ? Fmt.Host(_settings.NormalizedBaseUrl)
            : Fmt.Host(_previewServerUrl);
        AboutProjectsValue.Text = Loc.T("共 {0} 个项目 · {1} 条流水线",
            _state.MonitoredProjects.Count, _state.AllVisiblePipelines.Count);
        UpdateStatusText.Text = UpdateStatusMessage();
        UpdateStatusText.Foreground = UpdateStatusBrush();
        CheckUpdateButton.IsEnabled = !IsUpdateBusy();
        var latest = _updater.LatestVersion;
        UpdateNowButton.Visibility = string.IsNullOrEmpty(latest) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrEmpty(latest)) UpdateNowButton.Content = Loc.T("更新到 {0}", latest);
    }

    private bool IsUpdateBusy() => _updater.Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;

    private string UpdateStatusMessage() => _updater.Phase switch
    {
        UpdatePhase.Checking => Loc.T("正在检查更新…"),
        UpdatePhase.Downloading => Loc.T("正在下载更新… {0}%", _updater.DownloadPercent),
        UpdatePhase.Installing => Loc.T("正在安装更新…"),
        UpdatePhase.Failed => string.IsNullOrEmpty(_updater.PhaseMessage) ? Loc.T("检查更新失败") : _updater.PhaseMessage,
        UpdatePhase.UpToDate => Loc.T("已是最新版本"),
        _ => string.IsNullOrEmpty(_updater.LatestVersion)
            ? Loc.T("更新源：GitHub Release")
            : Loc.T("有新版本 {0}", _updater.LatestVersion)
    };

    private Brush UpdateStatusBrush()
    {
        if (_updater.Phase == UpdatePhase.Failed) return BrushFor("FailedBrush");
        if (!string.IsNullOrEmpty(_updater.LatestVersion)) return BrushFor("RunningBrush");
        return BrushFor("SecondaryTextBrush");
    }

    private Geometry? GeometryFrom(string key) => TryFindResource(key) as Geometry;

    private Brush BrushFor(string key) => TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Gray;

    // MARK: - 下拉项

    /// <summary>ComboBox 的一项：Value 供 SelectedValue 绑定，Text 随语言重算。</summary>
    private sealed class SettingsOption
    {
        private readonly Func<string> _label;

        public SettingsOption(object value, Func<string> label)
        {
            Value = value;
            _label = label;
            Text = label();
        }

        public object Value { get; }
        public string Text { get; private set; }

        public void RefreshText() => Text = _label();

        /// <summary>
        /// 没有模板时（容器没拿到 ContentTemplate）内容呈现器会退化成 ToString()，
        /// 所以这里直接返回显示文字 —— 免得下拉框里出现一长串 CLR 类型名。
        /// </summary>
        public override string ToString() => Text;
    }
}

/// <summary>
/// 项目页的一行：打星按钮 + 名称 / 命名空间 + 归档标记 + 相对时间 + 勾选 + 在浏览器打开。
/// GitLabProject 本身不带变更通知，所以用这层包一下，勾选和打星才能立刻反映到界面。
/// </summary>
public sealed class SettingsProjectRow : INotifyPropertyChanged
{
    private readonly SettingsWindow _owner;

    public SettingsProjectRow(SettingsWindow owner, GitLabProject project)
    {
        _owner = owner;
        Project = project;
    }

    public GitLabProject Project { get; }

    public string Name => Project.ShortPath;
    public string NamespaceText => Project.Namespace;
    public bool ShowNamespace => Project.Namespace.Length > 0;
    public bool Archived => Project.Archived;
    public string ArchivedLabel => Loc.T("归档");
    public string Time => Fmt.Relative(Project.LastActivityAt);
    public bool ShowTime => Project.LastActivityAt is not null;
    public bool Starred => Project.Starred;
    public string StarTip => Loc.T(Project.Starred ? "取消打星" : "打星");
    public string OpenTip => Loc.T("在项目页打开");
    public bool ShowCheckbox => !_owner.Settings.OnlyStarred;
    public bool IsBusy { get; private set; }

    public bool Monitored
    {
        get => _owner.Settings.MonitoredProjectIds.Contains(Project.Id);
        set
        {
            var ids = _owner.Settings.MonitoredProjectIds;
            if (value)
            {
                if (!ids.Contains(Project.Id)) _owner.Settings.SetMonitoredProjectIds(ids.Append(Project.Id));
            }
            else if (ids.Contains(Project.Id))
            {
                _owner.Settings.SetMonitoredProjectIds(ids.Where(id => id != Project.Id));
            }
            OnPropertyChanged(nameof(Monitored));
        }
    }

    public void RefreshMonitored() => OnPropertyChanged(nameof(Monitored));
    public void RefreshFlags()
    {
        OnPropertyChanged(nameof(ShowCheckbox));
        RefreshMonitored();
    }

    public void SetBusy(string? busyAction)
    {
        var busy = busyAction == $"star-{Project.Id}";
        if (busy == IsBusy) return;
        IsBusy = busy;
        OnPropertyChanged(nameof(IsBusy));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 静态文案的本地化附加属性：XAML 里写中文原文（loc:LocalizedText.Source="服务器地址"），
/// 加载时取译文；语言切换时由窗口调 ApplyTree 整棵树重刷，界面立刻换语言。
/// 模板里的行（项目行）不走这里，改由行的计算属性给文案。
/// </summary>
public static class LocalizedText
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(LocalizedText), new PropertyMetadata("", OnSourceChanged));

    public static void SetSource(DependencyObject element, string value) => element.SetValue(SourceProperty, value);

    public static string GetSource(DependencyObject element) => (string)element.GetValue(SourceProperty);

    private static void OnSourceChanged(DependencyObject element, DependencyPropertyChangedEventArgs e) => Apply(element);

    /// <summary>按元素类型把译文写到该显示的地方。</summary>
    public static void Apply(DependencyObject element)
    {
        var source = GetSource(element);
        if (string.IsNullOrEmpty(source)) return;
        var text = Loc.T(source);
        switch (element)
        {
            case TextBlock block: block.Text = text; break;
            case Button button: button.Content = text; break;
            case CheckBox check: check.Content = text; break;
            case TabItem item: item.Header = text; break;
            case ComboBoxItem item: item.Content = text; break;
            case Label label: label.Content = text; break;
            case GroupBox box: box.Header = text; break;
            // 搜索框：占位文字走 Tag —— SearchBox 样式模板里那个 TextBlock 绑的就是 Tag，
            // 模板没暴露占位符属性，用 Tag 传最省事，也不用在窗口里再画一个兄弟节点
            case TextBox box: box.Tag = text; break;
        }
    }

    /// <summary>递归整棵逻辑树重刷（未选中的标签页内容也在逻辑树里，所以会被刷到）。</summary>
    public static void ApplyTree(DependencyObject root)
    {
        Apply(root);
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject element)
                ApplyTree(element);
    }
}
