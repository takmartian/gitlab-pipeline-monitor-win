using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitLabPipelineMonitor.Core.Storage;

/// <summary>
/// 设置文件（%APPDATA%\GitLab Pipeline Monitor\settings.json）。
/// 用 JSON 而不是注册表：可读、可整体备份迁移，出问题能直接打开看。
/// 令牌不在这里 —— 它单独走加密文件。</summary>
public sealed class SettingsFile
{
    [JsonPropertyName("serverUrl")] public string ServerUrl { get; set; } = "";
    [JsonPropertyName("authMode")] public string AuthMode { get; set; } = "pat";
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";
    [JsonPropertyName("refreshInterval")] public int RefreshInterval { get; set; } = 120;
    [JsonPropertyName("pipelinesPerProject")] public int PipelinesPerProject { get; set; } = 5;
    [JsonPropertyName("onlyStarred")] public bool OnlyStarred { get; set; } = true;
    [JsonPropertyName("monitoredProjectIds")] public List<int> MonitoredProjectIds { get; set; } = new();
    [JsonPropertyName("showStatusBadge")] public bool ShowStatusBadge { get; set; } = true;
    [JsonPropertyName("showFailureCount")] public bool ShowFailureCount { get; set; } = true;
    [JsonPropertyName("notifyOnFailure")] public bool NotifyOnFailure { get; set; } = true;
    [JsonPropertyName("notifyOnRecovery")] public bool NotifyOnRecovery { get; set; }
    [JsonPropertyName("refreshOnOpen")] public bool RefreshOnOpen { get; set; } = true;
    [JsonPropertyName("panelWidth")] public int PanelWidth { get; set; } = 420;
    [JsonPropertyName("panelHeight")] public int? PanelHeight { get; set; }
    [JsonPropertyName("autoUpdateEnabled")] public bool AutoUpdateEnabled { get; set; } = true;
    [JsonPropertyName("ignoredUpdateVersion")] public string? IgnoredUpdateVersion { get; set; }
    [JsonPropertyName("autoRefreshPaused")] public bool AutoRefreshPaused { get; set; }

    /// <summary>屏幕右侧的浮标：托盘图标被收起来时，靠它看有没有流水线在跑。</summary>
    [JsonPropertyName("floatingBadgeEnabled")] public bool FloatingBadgeEnabled { get; set; } = true;

    /// <summary>浮标所在的显示器（设备名，如 \\.\DISPLAY1）。空 = 主屏。</summary>
    [JsonPropertyName("floatingBadgeScreen")] public string FloatingBadgeScreen { get; set; } = "";

    /// <summary>浮标纵向位置：工作区高度的比例（0 = 顶端，1 = 底端）。</summary>
    [JsonPropertyName("floatingBadgeTopRatio")] public double FloatingBadgeTopRatio { get; set; } = 0.5;

    /// <summary>上次成功检查更新的时间（24 小时内不再自动检查）。</summary>
    [JsonPropertyName("lastUpdateCheck")] public DateTime? LastUpdateCheck { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static SettingsFile Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new SettingsFile();
            return JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path), Options) ?? new SettingsFile();
        }
        catch (Exception)
        {
            // 文件坏了就用默认值起，别让用户打不开程序
            return new SettingsFile();
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}
