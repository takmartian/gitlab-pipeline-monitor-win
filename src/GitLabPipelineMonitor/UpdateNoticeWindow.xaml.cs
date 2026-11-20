using System.Windows;
using System.Windows.Media;
using GitLabPipelineMonitor.Core.Localization;
using GitLabPipelineMonitor.Core.Update;

namespace GitLabPipelineMonitor;

/// <summary>
/// 检测到新版本时的弹窗：展示版本号和更新内容，让用户选「立即更新」还是「忽略此版本」。
/// 用独立窗口而不是消息框，是因为更新说明通常较长，需要能滚动和复制。
/// </summary>
public partial class UpdateNoticeWindow : Window
{
    public Action? OnUpdate { get; set; }
    public Action? OnIgnore { get; set; }

    public UpdateNoticeWindow(GitHubRelease release, string currentVersion, string notes)
    {
        InitializeComponent();

        Title = Loc.T("更新");
        TitleText.Text = Loc.T("有新版本 {0}", release.Version);
        CurrentText.Text = Loc.T("当前版本 {0}", currentVersion);
        UpdateButton.Content = Loc.T("立即更新");
        IgnoreButton.Content = Loc.T("忽略此版本");

        NotesText.Text = string.IsNullOrWhiteSpace(notes) ? Loc.T("这个版本没有写更新说明。") : notes;
        // 说明从开头读起：ScrollViewer 会自动停在末尾的情况出现过
        Loaded += (_, _) => NotesText.ScrollToHome();
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var callback = OnUpdate;
        Close();
        callback?.Invoke();
    }

    private void IgnoreButton_Click(object sender, RoutedEventArgs e)
    {
        var callback = OnIgnore;
        Close();
        callback?.Invoke();
    }
}
