using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using GitLabPipelineMonitor.Core;
using GitLabPipelineMonitor.Core.GitLab;
using GitLabPipelineMonitor.Core.Localization;
using GitLabPipelineMonitor.Core.Storage;
using GitLabPipelineMonitor.Core.Update;

namespace GitLabPipelineMonitor;

public enum UpdatePhase
{
    Idle,
    Checking,
    Downloading,
    Installing,
    UpToDate,
    Failed
}

/// <summary>
/// 自研更新器：GitHub Release 就是更新源，不需要额外的服务器或对象存储。
/// 流程：查 Releases API → 比版本 → 弹窗（立即更新 / 忽略此版本）→ 下载 zip →
/// 用 GitHub 给的 sha256 校验 → 解压 → 起一个独立 helper 进程（等本进程退出后替换文件、
/// 启动新版本）→ 本进程退出。零第三方依赖，和 macOS 版同一套设计。
/// </summary>
public sealed class UpdateManager : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly WindowsHost _host;
    private readonly HttpClient _http;
    private DispatcherTimer? _timer;

    /// <summary>本次运行已经提示过的版本：同一个版本不反复弹窗。</summary>
    private string? _notifiedVersion;

    private UpdatePhase _phase = UpdatePhase.Idle;
    public UpdatePhase Phase { get => _phase; private set => Set(ref _phase, value); }

    private string? _phaseMessage;
    /// <summary>失败原因等需要展示给用户的补充文字。</summary>
    public string? PhaseMessage { get => _phaseMessage; private set => Set(ref _phaseMessage, value); }

    private string? _latestVersion;
    public string? LatestVersion { get => _latestVersion; private set => Set(ref _latestVersion, value); }

    private GitHubRelease? _release;
    public GitHubRelease? Release { get => _release; private set => Set(ref _release, value); }

    private int _downloadPercent;
    public int DownloadPercent { get => _downloadPercent; private set => Set(ref _downloadPercent, value); }

    public string CurrentVersion => AppInfo.Version;

    public UpdateManager(AppSettings settings, WindowsHost host)
    {
        _settings = settings;
        _host = host;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"GitLabPipelineMonitorWin/{AppInfo.Version}");
    }

    /// <summary>启动后延迟几秒查一次，之后每 6 小时看一眼（本地缓存 24 小时，不会碰限流）。</summary>
    public void Start()
    {
        _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(async _ =>
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => CheckAsync(force: false)));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _timer.Tick += async (_, _) => await CheckAsync(force: false);
        _timer.Start();

        // 刚把开关打开就顺手查一次，不用等下一个周期
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.AutoUpdateEnabled) && _settings.AutoUpdateEnabled)
                _ = CheckAsync(force: false);
        };
    }

    /// <summary>检查更新。自动检查受「通用 - 自动更新」开关控制；手动检查（force）不受影响。</summary>
    public async Task CheckAsync(bool force)
    {
        if (Phase == UpdatePhase.Checking) return;
        if (!force && !_settings.AutoUpdateEnabled) return;
        if (!force && _settings.LastUpdateCheck is { } last
            && DateTime.Now - last < TimeSpan.FromHours(24)) return;

        Phase = UpdatePhase.Checking;
        PhaseMessage = null;
        try
        {
            var latest = await FetchLatestReleaseAsync().ConfigureAwait(false);
            Release = latest;
            _settings.LastUpdateCheck = DateTime.Now;

            if (AppVersion.IsNewer(latest.Version, CurrentVersion))
            {
                LatestVersion = latest.Version;
                Phase = UpdatePhase.Idle;
                HandleUpdateFound(latest.Version, automatic: !force);
            }
            else
            {
                LatestVersion = null;
                Phase = UpdatePhase.UpToDate;
            }
        }
        catch (Exception error)
        {
            // 自动检查失败不打扰用户；手动检查才报错
            LatestVersion = null;
            if (force)
            {
                Phase = UpdatePhase.Failed;
                PhaseMessage = Describe(error);
            }
            else
            {
                Phase = UpdatePhase.Idle;
            }
        }
    }

    private async Task<GitHubRelease> FetchLatestReleaseAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AppInfo.ReleaseApiUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
                throw new GitLabException(GitLabErrorKind.RateLimited);
            if (!response.IsSuccessStatusCode)
                throw new GitLabException(GitLabErrorKind.Http, ((int)response.StatusCode).ToString());

            using var document = System.Text.Json.JsonDocument.Parse(body);
            return GitHubRelease.FromJson(document.RootElement.Clone());
        }
        catch (GitLabException)
        {
            throw;
        }
        catch (HttpRequestException error)
        {
            throw new GitLabException(GitLabErrorKind.Network, error.Message);
        }
        catch (TaskCanceledException)
        {
            throw new GitLabException(GitLabErrorKind.Network, "timeout");
        }
    }

    private static string Describe(Exception error) => error switch
    {
        GitLabException { Kind: GitLabErrorKind.Network } => Loc.T("无法连接 GitHub（可能被网络或代理挡住）"),
        GitLabException { Kind: GitLabErrorKind.RateLimited } => Loc.T("GitHub 接口访问过于频繁，稍后再试"),
        GitLabException { Kind: GitLabErrorKind.Decoding } => Loc.T("数据解析失败，可能不是 GitLab 服务器"),
        GitLabException { Message: { Length: > 0 } message } => message,
        _ => error.Message
    };

    /// <summary>发现新版本：自动检查才受「忽略此版本」影响；手动检查一律把内容摆出来。</summary>
    private void HandleUpdateFound(string version, bool automatic)
    {
        if (automatic)
        {
            if (_settings.IgnoredUpdateVersion == version) return;
            if (_notifiedVersion == version) return;
        }
        _notifiedVersion = version;
        PresentUpdateNotice();
    }

    /// <summary>手动检查完给个明确反馈。</summary>
    public void PresentCheckResult()
    {
        switch (Phase)
        {
            case UpdatePhase.UpToDate:
                NotifyUser(Loc.T("已是最新版本"), null);
                return;
            case UpdatePhase.Failed:
                NotifyUser(Loc.T("检查更新失败"), PhaseMessage);
                return;
            default:
                if (Release is not null && LatestVersion is not null) PresentUpdateNotice();
                return;
        }
    }

    private static void NotifyUser(string title, string? body)
        => System.Windows.MessageBox.Show(string.IsNullOrEmpty(body) ? title : $"{title}\n{body}",
            AppInfo.Name, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

    /// <summary>弹更新窗口：展示更新内容，让用户选「立即更新」还是「忽略此版本」。</summary>
    public void PresentUpdateNotice()
    {
        var release = Release;
        if (release is null) return;
        var window = new UpdateNoticeWindow(release, CurrentVersion, UpdateNotes.Plain(release.Body))
        {
            OnUpdate = () =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await InstallAsync().ConfigureAwait(true);
                });
            },
            OnIgnore = () =>
            {
                _settings.IgnoredUpdateVersion = release.Version;
            }
        };
        window.Show();
        window.Activate();
    }

    // MARK: - 安装

    public async Task InstallAsync()
    {
        var release = Release;
        var asset = release?.PreferredAsset;
        if (release is null || asset is null) return;
        if (Phase is UpdatePhase.Downloading or UpdatePhase.Installing) return;

        Phase = UpdatePhase.Downloading;
        PhaseMessage = null;
        var work = Path.Combine(Path.GetTempPath(), "glpm-update-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(work);
            var zipPath = Path.Combine(work, "update.zip");

            await DownloadWithProgressAsync(asset.BrowserDownloadUrl, zipPath).ConfigureAwait(false);
            if (string.IsNullOrEmpty(asset.Digest) || !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                throw new GitLabException(GitLabErrorKind.Decoding, "missing digest");

            var expected = asset.Digest[7..].ToLowerInvariant();
            var actual = await ComputeSha256Async(zipPath).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new GitLabException(GitLabErrorKind.Decoding, "checksum");

            Phase = UpdatePhase.Installing;
            var extracted = Path.Combine(work, "extracted");
            Directory.CreateDirectory(extracted);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extracted, true);

            var newExe = FindExecutable(extracted);
            if (newExe is null) throw new GitLabException(GitLabErrorKind.Decoding, "no exe");

            LaunchHelperAndExit(newExe.FullName, work);
        }
        catch (Exception error)
        {
            Phase = UpdatePhase.Failed;
            PhaseMessage = error is GitLabException { Kind: GitLabErrorKind.Decoding } && error.Message == "checksum"
                ? Loc.T("下载校验失败，已放弃更新")
                : error is GitLabException { Kind: GitLabErrorKind.Network }
                    ? Loc.T("无法连接 GitHub（可能被网络或代理挡住）")
                    : error.Message;
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    private async Task DownloadWithProgressAsync(string url, string destination)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
            throw new GitLabException(GitLabErrorKind.InvalidUrl);

        // 下载走一个不限时的客户端：自包含包 100+ MB，20 秒的默认超时会不够
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"GitLabPipelineMonitorWin/{AppInfo.Version}");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new GitLabException(GitLabErrorKind.Http, ((int)response.StatusCode).ToString());

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            written += read;
            if (total > 0)
            {
                var percent = (int)(written * 100 / total);
                if (percent != DownloadPercent)
                    _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => DownloadPercent = percent);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static FileInfo? FindExecutable(string directory)
    {
        // zip 里通常是 publish 出来的单个 exe；也可能多一层目录
        var direct = new DirectoryInfo(directory).EnumerateFiles("*.exe", SearchOption.AllDirectories)
            .Where(f => !f.Name.Contains("uninstaller", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Length)
            .ToList();
        return direct.Count > 0 ? direct[0] : null;
    }

    /// <summary>
    /// Windows 上不能删除正在运行的 exe，但可以重命名它。所以让一个独立的 PowerShell
    /// 进程等本进程退出 → 把旧 exe 改名 → 拷入新文件 → 启动新版本 → 删掉改名后的旧包。
    /// 本进程随后退出，替换过程不受文件占用影响。
    /// </summary>
    private void LaunchHelperAndExit(string newExePath, string workDirectory)
    {
        var currentExe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(currentExe)) throw new GitLabException(GitLabErrorKind.Server, "no current path");

        // 逐字字符串 + 占位符替换：PowerShell 脚本里满是 { } 和 " ，
        // 用内插原始字符串字面量要数大括号，容易写错，这里直接用最朴素的写法。
        var script = @"
            $ErrorActionPreference = 'Stop'
            $current = '@@CURRENT@@'
            $new = '@@NEW@@'
            $work = '@@WORK@@'
            $procId = @@PID@@
            try {
                for ($i = 0; $i -lt 150; $i++) {
                    if (-not (Get-Process -Id $procId -ErrorAction SilentlyContinue)) { break }
                    Start-Sleep -Milliseconds 200
                }
                $old = ""$current.old""
                if (Test-Path $old) { Remove-Item $old -Force -ErrorAction SilentlyContinue }
                Move-Item -LiteralPath $current -Destination $old -Force
                Copy-Item -LiteralPath $new -Destination $current -Force
                Start-Process -FilePath $current
                Start-Sleep -Seconds 2
                Remove-Item -LiteralPath $old -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
            } catch {
                # 替换失败至少把人叫回来：把旧包改回去再启动
                try { if (Test-Path $old) { Move-Item -LiteralPath $old -Destination $current -Force } } catch {}
                try { Start-Process -FilePath $current } catch {}
            }
            ";
        script = script
            .Replace("@@CURRENT@@", Escape(currentExe))
            .Replace("@@NEW@@", Escape(newExePath))
            .Replace("@@WORK@@", Escape(workDirectory))
            .Replace("@@PID@@", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));


        var scriptPath = Path.Combine(workDirectory, "install.ps1");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            // 路径可能带空格，用引号包住再交给命令行
            Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + (char)34 + scriptPath + (char)34,
            UseShellExecute = true
        });

        System.Windows.Application.Current.Shutdown();
    }

    private static string Escape(string value) => value.Replace("'", "''");

    public void Dispose()
    {
        // DispatcherTimer 不实现 IDisposable，停掉即可
        _timer?.Stop();
        _http.Dispose();
    }
}
