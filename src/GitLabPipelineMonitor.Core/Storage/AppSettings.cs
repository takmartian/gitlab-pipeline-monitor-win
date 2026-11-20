using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Localization;
using GitLabPipelineMonitor.Core.Platform;

namespace GitLabPipelineMonitor.Core.Storage;

/// <summary>
/// 应用设置：内存里是唯一的真相来源，改动即时生效并立刻落盘（不需要「保存」按钮）。
/// 令牌是例外 —— 它按服务器主机名分账户，走加密文件，不进 settings.json。
/// 这里的账户切换逻辑与 macOS 版逐条对齐，包括「地址是逐字输入的」那层保护。</summary>
public sealed class AppSettings : ObservableObject
{
    public static readonly int[] RefreshIntervals = { 0, 30, 60, 120, 300, 600 };
    public static readonly int[] PanelWidths = { 380, 420, 460, 520 };
    public const int DefaultPanelHeight = 560;
    public const int MinPanelHeight = 320;

    private readonly TokenStore _tokenStore;
    private readonly string _settingsPath;
    private SettingsFile _file;

    /// <summary>令牌归属的账户（跟随服务器地址变化）。</summary>
    private string _tokenAccount = "default";

    /// <summary>程序内部改写令牌时置真：切账户读回来的空值不是「用户清空令牌」，不该去删存储。</summary>
    private bool _programmaticToken;

    /// <summary>正在拖面板底边：这期间高度频繁变化，先不落盘。</summary>
    private bool _adjustingPanelHeight;

    public AppSettings(IHostServices host, string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);
        _settingsPath = Path.Combine(root, "settings.json");
        _tokenStore = new TokenStore(host, root);
        _file = SettingsFile.Load(_settingsPath);

        _serverUrl = _file.ServerUrl;
        _authMode = GitLabAuthModeExtensions.Parse(_file.AuthMode);
        _language = _file.Language switch { "zh" => AppLanguage.Zh, "en" => AppLanguage.En, _ => AppLanguage.Auto };
        _refreshInterval = _file.RefreshInterval;
        _pipelinesPerProject = _file.PipelinesPerProject;
        _onlyStarred = _file.OnlyStarred;
        _monitoredProjectIds = new List<int>(_file.MonitoredProjectIds);
        _showStatusBadge = _file.ShowStatusBadge;
        _showFailureCount = _file.ShowFailureCount;
        _notifyOnFailure = _file.NotifyOnFailure;
        _notifyOnRecovery = _file.NotifyOnRecovery;
        _refreshOnOpen = _file.RefreshOnOpen;
        _panelWidth = PanelWidths.Contains(_file.PanelWidth) ? _file.PanelWidth : 420;
        _panelHeight = ClampHeight(_file.PanelHeight ?? DefaultPanelHeight);
        _autoUpdateEnabled = _file.AutoUpdateEnabled;
        _ignoredUpdateVersion = _file.IgnoredUpdateVersion;
        _autoRefreshPaused = _file.AutoRefreshPaused;

        Loc.Language = _language;
        _tokenAccount = ServerAddress.StrictAccount(_serverUrl) ?? "default";
        _token = _tokenStore.Password(_tokenAccount) ?? "";
    }

    public TokenStore TokenStore => _tokenStore;
    public string SettingsPath => _settingsPath;

    // MARK: - 连接

    private string _serverUrl;
    public string ServerUrl
    {
        get => _serverUrl;
        set
        {
            var next = value ?? "";
            if (!Set(ref _serverUrl, next)) return;
            SaveNow();
            // 地址是逐字输入的：还没形成合法主机名时先别动令牌账户，
            // 否则每敲一个字符都会把内存里的令牌当成「该账户没有令牌」清空，面板会闪「未配置」
            var account = ServerAddress.StrictAccount(next);
            if (account is null || account == _tokenAccount) return;
            var current = Token;
            _tokenAccount = account;
            var stored = _tokenStore.Password(account);
            if (!string.IsNullOrWhiteSpace(stored)) ApplyToken(stored!);          // 切到该实例自己的令牌
            else if (!string.IsNullOrWhiteSpace(current)) _tokenStore.Set(current, account);  // 当前令牌跟着新地址走，不丢
        }
    }

    private GitLabAuthMode _authMode;
    public GitLabAuthMode AuthMode
    {
        get => _authMode;
        set
        {
            if (!Set(ref _authMode, value)) return;
            _file.AuthMode = value.ToStorageString();
            SaveNow();
        }
    }

    private string _token = "";

    /// <summary>明文只在内存里，落盘走加密文件。</summary>
    public string Token
    {
        get => _token;
        set
        {
            var next = value ?? "";
            if (!Set(ref _token, next) || _programmaticToken) return;
            if (next.Length == 0) _tokenStore.Remove(_tokenAccount);
            else _tokenStore.Set(next, _tokenAccount);
        }
    }

    /// <summary>程序内部赋值令牌（切换实例等），不触发落盘也不删条目。</summary>
    public void ApplyToken(string value)
    {
        _programmaticToken = true;
        Token = value;
        _programmaticToken = false;
    }

    public string TokenAccount => _tokenAccount;
    // MARK: - 通用

    private AppLanguage _language;
    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (!Set(ref _language, value)) return;
            _file.Language = value switch { AppLanguage.Zh => "zh", AppLanguage.En => "en", _ => "auto" };
            Loc.Language = value;
            SaveNow();
            // 语言变了，所有已经显示出去的文案都要重算
            OnPropertyChanged(null);
        }
    }

    private int _refreshInterval;

    /// <summary>秒；0 = 关闭自动刷新。</summary>
    public int RefreshInterval
    {
        get => _refreshInterval;
        set
        {
            if (!Set(ref _refreshInterval, value)) return;
            _file.RefreshInterval = value;
            SaveNow();
        }
    }

    private int _pipelinesPerProject;
    public int PipelinesPerProject
    {
        get => _pipelinesPerProject;
        set
        {
            if (!Set(ref _pipelinesPerProject, Math.Clamp(value, 1, 20))) return;
            _file.PipelinesPerProject = _pipelinesPerProject;
            SaveNow();
        }
    }

    private bool _onlyStarred;
    public bool OnlyStarred
    {
        get => _onlyStarred;
        set
        {
            if (!Set(ref _onlyStarred, value)) return;
            _file.OnlyStarred = value;
            SaveNow();
        }
    }

    private List<int> _monitoredProjectIds;
    public IReadOnlyList<int> MonitoredProjectIds => _monitoredProjectIds;

    public void SetMonitoredProjectIds(IEnumerable<int> ids)
    {
        var next = ids.Distinct().OrderBy(id => id).ToList();
        if (next.SequenceEqual(_monitoredProjectIds)) return;
        _monitoredProjectIds = next;
        _file.MonitoredProjectIds = new List<int>(next);
        SaveNow();
        OnPropertyChanged(nameof(MonitoredProjectIds));
    }

    private bool _showStatusBadge;
    public bool ShowStatusBadge
    {
        get => _showStatusBadge;
        set
        {
            if (!Set(ref _showStatusBadge, value)) return;
            _file.ShowStatusBadge = value;
            SaveNow();
        }
    }

    private bool _showFailureCount;
    public bool ShowFailureCount
    {
        get => _showFailureCount;
        set
        {
            if (!Set(ref _showFailureCount, value)) return;
            _file.ShowFailureCount = value;
            SaveNow();
        }
    }

    private bool _notifyOnFailure;
    public bool NotifyOnFailure
    {
        get => _notifyOnFailure;
        set
        {
            if (!Set(ref _notifyOnFailure, value)) return;
            _file.NotifyOnFailure = value;
            SaveNow();
        }
    }

    private bool _notifyOnRecovery;
    public bool NotifyOnRecovery
    {
        get => _notifyOnRecovery;
        set
        {
            if (!Set(ref _notifyOnRecovery, value)) return;
            _file.NotifyOnRecovery = value;
            SaveNow();
        }
    }

    private bool _refreshOnOpen;
    public bool RefreshOnOpen
    {
        get => _refreshOnOpen;
        set
        {
            if (!Set(ref _refreshOnOpen, value)) return;
            _file.RefreshOnOpen = value;
            SaveNow();
        }
    }

    /// <summary>右键菜单可以临时暂停自动刷新，这个状态要记住（下次启动还是暂停）。</summary>
    private bool _autoRefreshPaused;
    public bool AutoRefreshPaused
    {
        get => _autoRefreshPaused;
        set
        {
            if (!Set(ref _autoRefreshPaused, value)) return;
            _file.AutoRefreshPaused = value;
            SaveNow();
        }
    }

    private int _panelWidth;
    public int PanelWidth
    {
        get => _panelWidth;
        set
        {
            if (!PanelWidths.Contains(value)) return;
            if (!Set(ref _panelWidth, value)) return;
            _file.PanelWidth = value;
            SaveNow();
        }
    }

    private int _panelHeight;

    /// <summary>面板高度：拖底边实时改，这里是唯一来源。</summary>
    public int PanelHeight
    {
        get => _panelHeight;
        set
        {
            var clamped = ClampHeight(value);
            if (!Set(ref _panelHeight, clamped)) return;
            // 拖动过程中不落盘：一次拖拽会有上百次写入，松手后存一次就够了
            if (!_adjustingPanelHeight) SaveNow();
            PanelHeightChanged?.Invoke(_panelHeight);
        }
    }

    /// <summary>高度变化回调（窗口据此同步尺寸）。</summary>
    public event Action<int>? PanelHeightChanged;

    public void BeginPanelHeightAdjustment() => _adjustingPanelHeight = true;

    public void EndPanelHeightAdjustment()
    {
        if (!_adjustingPanelHeight) return;
        _adjustingPanelHeight = false;
        SaveNow();
    }

    /// <summary>最高不能超过工作区高度：底边一旦拖出屏幕就再也抓不回来。</summary>
    public static int ClampHeight(int height, int? workAreaHeight = null)
    {
        var max = Math.Max(MinPanelHeight, (workAreaHeight ?? 1000) - 12);
        return Math.Min(Math.Max(height, MinPanelHeight), max);
    }

    // MARK: - 更新

    private bool _autoUpdateEnabled;
    public bool AutoUpdateEnabled
    {
        get => _autoUpdateEnabled;
        set
        {
            if (!Set(ref _autoUpdateEnabled, value)) return;
            _file.AutoUpdateEnabled = value;
            SaveNow();
        }
    }

    /// <summary>用户选了「忽略此版本」的版本号；比它新的版本照样会提示。</summary>
    private string? _ignoredUpdateVersion;
    public string? IgnoredUpdateVersion
    {
        get => _ignoredUpdateVersion;
        set
        {
            if (Set(ref _ignoredUpdateVersion, value)) _file.IgnoredUpdateVersion = value;
            SaveNow();
        }
    }

    public DateTime? LastUpdateCheck
    {
        get => _file.LastUpdateCheck;
        set
        {
            _file.LastUpdateCheck = value;
            SaveNow();
        }
    }

    // MARK: - 派生

    /// <summary>规范化后的服务器地址；没填好是 null。</summary>
    public string? NormalizedBaseUrl => ServerAddress.Normalize(_serverUrl);

    /// <summary>演示模式：不联网也能预览界面。</summary>
    public bool DemoMode { get; set; }

    public bool IsConfigured => DemoMode || (NormalizedBaseUrl is not null && Token.Trim().Length > 0);

    public static string RefreshIntervalLabel(int seconds) => seconds switch
    {
        0 => Localization.Loc.T("从不"),
        30 => Localization.Loc.T("每 {0} 秒", 30),
        60 => Localization.Loc.T("每分钟"),
        120 => Localization.Loc.T("每 2 分钟"),
        300 => Localization.Loc.T("每 5 分钟"),
        600 => Localization.Loc.T("每 10 分钟"),
        _ => Localization.Loc.T("每 {0} 秒", seconds)
    };

    // MARK: - 落盘

    private void SaveNow()
    {
        _file.ServerUrl = _serverUrl;
        _file.PanelHeight = _panelHeight;
        try
        {
            _file.Save(_settingsPath);
        }
        catch (Exception)
        {
            // 盘上写不进去（只读目录、磁盘满）不该让程序崩：内存里的值仍然有效
        }
    }
}
