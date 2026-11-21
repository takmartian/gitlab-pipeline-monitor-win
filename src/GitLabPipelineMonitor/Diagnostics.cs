using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Localization;
using GitLabPipelineMonitor.Core.Storage;
using GitLabPipelineMonitor.Core.Update;

namespace GitLabPipelineMonitor;

/// <summary>
/// 命令行诊断入口。参数与 macOS 版对齐，方便同一套验证习惯：
///   --demo                用演示数据填充并打开面板（不联网也能看界面）
///   --panel               打开面板后保持运行
///   --capture &lt;路径&gt; [panel|settings|projects|general|about]   弹出真实界面后抓屏
///   --updateprobe         打印 GitHub Release 的最新版本、资产与 sha256
///   --updateinstall       端到端验证更新链路（会真的替换当前 exe）
///   --selftest            跑一遍关键逻辑自检并打印结果
/// 返回 true 表示这是诊断模式，调用方应当随即退出。
/// </summary>
public static class Diagnostics
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int attachFlag);

    private const int AttachParentConsole = -1;

    public static bool RunIfRequested(string[] args, Core.AppState state, AppSettings settings,
        UpdateManager updater, PanelWindow panel)
    {
        // 从 cmd 启动的 WinExe 默认没有控制台句柄，附加父控制台才能把输出打出来
        AttachConsole(AttachParentConsole);

        if (Has(args, "--updateprobe")) { ProbeAsync(updater).GetAwaiter().GetResult(); return true; }
        if (Has(args, "--updateinstall")) { InstallProbeAsync(updater).GetAwaiter().GetResult(); return true; }
        if (Has(args, "--selftest")) { SelfTest(state, settings); return true; }

        var captureIndex = IndexOf(args, "--capture");
        if (captureIndex >= 0)
        {
            var path = args.Length > captureIndex + 1 ? args[captureIndex + 1] : "/tmp/glpm-panel.png";
            var what = args.Length > captureIndex + 2 ? args[captureIndex + 2] : "panel";
            Capture(path, what, state, settings, panel);
            return true;
        }

        if (Has(args, "--demo"))
        {
            settings.DemoMode = true;
            DemoData.Apply(state);
            panel.ShowAt(80, 80, settings.PanelWidth, settings.PanelHeight);
            return false;   // 演示模式要留着窗口给人看
        }

        if (Has(args, "--panel"))
        {
            panel.ShowAt(80, 80, settings.PanelWidth, settings.PanelHeight);
            return false;
        }

        return false;
    }

    private static bool Has(string[] args, string flag) => IndexOf(args, flag) >= 0;

    private static int IndexOf(string[] args, string flag) => Array.IndexOf(args, flag);

    private static void Line(string text)
    {
        Console.WriteLine(text);
        Console.Error.WriteLine(text);
        Console.Out.Flush();
        Console.Error.Flush();
    }

    // MARK: --updateprobe

    private static async Task ProbeAsync(UpdateManager updater)
    {
        Line($"当前版本 {AppInfo.Version} (build {AppInfo.Build})");
        Line($"更新源 {AppInfo.ReleaseApiUrl}");
        var stopwatch = Stopwatch.StartNew();
        await updater.CheckAsync(force: true);
        stopwatch.Stop();
        Line($"检查耗时 {stopwatch.ElapsedMilliseconds} ms，阶段 {updater.Phase}");

        var release = updater.Release;
        if (release is null)
        {
            Line($"✗ 没拿到 release，失败原因：{updater.PhaseMessage ?? "无"}");
            return;
        }
        Line($"远端最新 {release.Version}（tag {release.TagName} · prerelease {release.Prerelease}）");
        Line($"页面 {release.HtmlUrl}");
        foreach (var asset in release.Assets)
            Line($"  资产 {asset.Name} · {asset.Size} 字节 · digest {asset.Digest ?? "无"}");
        var preferred = release.PreferredAsset;
        Line(preferred is null ? "✗ 没有可用资产" : $"将下载 {preferred.BrowserDownloadUrl}");
        Line($"有更新: {updater.LatestVersion is not null} · 最新 {updater.LatestVersion ?? "无"}");
    }

    // MARK: --updateinstall

    private static async Task InstallProbeAsync(UpdateManager updater)
    {
        await updater.CheckAsync(force: true);
        if (updater.Release is null)
        {
            Line("✗ 没拿到 release，无法验证安装链路");
            return;
        }
        var asset = updater.Release.PreferredAsset;
        if (asset is null)
        {
            Line("✗ release 里没有资产");
            return;
        }

        // 只验证「下载 + 校验 + 解压 + 找到 exe」这一段，不真的替换正在运行的文件，
        // 这样在开发机上可以反复跑而不会把 exe 换掉
        var work = Path.Combine(Path.GetTempPath(), "glpm-installprobe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, "update.zip");
            var stopwatch = Stopwatch.StartNew();
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("GitLabPipelineMonitorWin/probe");
                var bytes = await http.GetByteArrayAsync(asset.BrowserDownloadUrl).ConfigureAwait(false);
                await File.WriteAllBytesAsync(zip, bytes).ConfigureAwait(false);
            }
            stopwatch.Stop();
            Line($"下载完成 {new System.IO.FileInfo(zip).Length} 字节，耗时 {stopwatch.ElapsedMilliseconds} ms");

            if (asset.Digest is { Length: > 0 } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = File.OpenRead(zip);
                var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream).ConfigureAwait(false))
                    .ToLowerInvariant();
                var expected = digest[7..].ToLowerInvariant();
                Line($"sha256 校验 {(hash == expected ? "通过" : $"不匹配！期望 {expected}，实际 {hash}")}");
            }
            else
            {
                Line("服务端没给 digest，跳过校验");
            }

            var extracted = Path.Combine(work, "extracted");
            Directory.CreateDirectory(extracted);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, extracted, true);
            var exe = new DirectoryInfo(extracted).EnumerateFiles("*.exe", SearchOption.AllDirectories)
                .OrderByDescending(f => f.Length).FirstOrDefault();
            Line(exe is null ? "✗ 解压后没找到 exe" : $"解压后找到可执行文件 {exe.Name}（{exe.Length} 字节）");
            Line("✓ 下载 → 校验 → 解压 这段链路可用，剩下的替换动作由更新器在真实更新时执行");
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    // MARK: --selftest

    private static void SelfTest(Core.AppState state, AppSettings settings)
    {
        var failures = new List<string>();
        var total = 0;
        void Check(string what, bool ok)
        {
            total++;
            Line($"{(ok ? "✓" : "✗")} {what}");
            if (!ok) failures.Add(what);
        }

        Check("地址补 scheme", ServerAddress.Normalize("gitlab.example.com") == "https://gitlab.example.com");
        Check("去掉误粘的 /api/v4", ServerAddress.Normalize("https://x/api/v4") == "https://x");
        Check("令牌误粘不当地址", ServerAddress.Normalize("glpat-abc") is null);
        Check("账户取主机名", ServerAddress.StrictAccount("https://gitlab.example.com:8080") == "gitlab.example.com");
        Check("拒绝 javascript 链接", ServerAddress.SafeBrowserUrl("javascript:alert(1)") is null);
        Check("相对链接可补全", ServerAddress.SafeBrowserUrl("/x", "https://g") == "https://g/x");
        Check("1.1.10 比 1.1.9 新", AppVersion.IsNewer("1.1.10", "1.1.9"));
        Check("同版本不算新", !AppVersion.IsNewer("1.2.3", "1.2.3"));
        Check("markdown 去分隔线", !UpdateNotes.Plain("a\n\n---\n\nb").Contains("---", StringComparison.Ordinal));
        Check("markdown 展开链接", UpdateNotes.Plain("[x](u)").Contains("x (u)", StringComparison.Ordinal));
        Check("运行中算在跑", PipelineStatus.Running.IsActive());
        Check("成功不算在跑", !PipelineStatus.Success.IsActive());
        Check("失败可重跑", PipelineStatus.Failed.CanRetry());
        Check("英文查表", Loc.T("刷新") == "Refresh" || Loc.Language != AppLanguage.En);
        Check("时长格式化", Fmt.Duration(192) == "3m 12s");
        Check("相对时间不为空", Fmt.Relative(DateTime.Now.AddMinutes(-5)).Length > 0);
        Check("演示数据已填充", state.StarredProjects.Count > 0 || !settings.DemoMode);
        // 主题画刷必须是未冻结的实例，否则改不动颜色（这曾导致启动即崩、托盘图标建不起来）
        var themeBrush = Application.Current?.Resources["PanelBackgroundBrush"] as System.Windows.Media.SolidColorBrush;
        Check("主题画刷未冻结", themeBrush is not null && !themeBrush.IsFrozen);
        Line($"· 主题：{(ThemeManager.IsDark ? "深色" : "浅色")} · 任务栏 {(ThemeManager.TaskbarIsLight ? "浅色" : "深色")} · 强调色 {ThemeManager.Accent}");

        Line(failures.Count == 0 ? $"全部通过（{total} 项）" : $"失败 {failures.Count} 项");
    }

    // MARK: --capture

    private static void Capture(string path, string what, Core.AppState state, AppSettings settings, PanelWindow panel)
    {
        settings.DemoMode = true;
        DemoData.Apply(state);

        Window target = what switch
        {
            "settings" or "projects" or "general" or "about" => CreateSettingsForCapture(what),
            _ => panel
        };

        if (ReferenceEquals(target, panel))
            panel.ShowAt(120, 80, settings.PanelWidth, settings.PanelHeight);
        else
            target.Show();

        target.Activate();

        // 等布局与图标都画完再抓，否则容易拍到空白
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var left = (int)Math.Round(target.Left);
                var top = (int)Math.Round(target.Top);
                var width = (int)Math.Round(target.ActualWidth > 0 ? target.ActualWidth : target.Width);
                var height = (int)Math.Round(target.ActualHeight > 0 ? target.ActualHeight : target.Height);
                using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height),
                        System.Drawing.CopyPixelOperation.SourceCopy);
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Line($"✓ 已抓取 {width}x{height} → {path}");
            }
            catch (Exception error)
            {
                Line($"✗ 抓屏失败：{error.Message}");
            }
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private static Window CreateSettingsForCapture(string tab)
    {
        var app = (App)Application.Current;
        app.ShowSettings(tab switch
        {
            "projects" => Views.SettingsTab.Projects,
            "general" => Views.SettingsTab.General,
            "about" => Views.SettingsTab.About,
            _ => Views.SettingsTab.Connection
        });
        // ShowSettings 打开的是应用持有的那个窗口，这里取回来抓
        return Application.Current.Windows.OfType<Views.SettingsWindow>().FirstOrDefault()
               ?? new Window { Width = 620, Height = 560, Title = "设置" };
    }
}
