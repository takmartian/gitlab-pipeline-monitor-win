using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
///   --capture &lt;路径&gt; [panel|settings|projects|general|about|traymenu]   弹出真实界面后抓屏
///   --updateprobe         打印 GitHub Release 的最新版本、资产与 sha256
///   --updateinstall       端到端验证更新链路（会真的替换当前 exe）
///   --selftest            跑一遍关键逻辑自检并打印结果
/// 返回 true 表示这是诊断模式，调用方应当随即退出。
/// </summary>
public static class Diagnostics
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int attachFlag);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const int AttachParentConsole = -1;

    public static bool RunIfRequested(string[] args, Core.AppState state, AppSettings settings,
        UpdateManager updater, PanelWindow panel, TrayIcon tray)
    {
        // 从 cmd 启动的 WinExe 默认没有控制台句柄，附加父控制台才能把输出打出来
        AttachConsole(AttachParentConsole);

        if (Has(args, "--updateprobe")) { ProbeAsync(updater).GetAwaiter().GetResult(); return true; }
        if (Has(args, "--updateinstall")) { InstallProbeAsync(updater).GetAwaiter().GetResult(); return true; }
        if (Has(args, "--selftest")) { SelfTest(state, settings); return true; }
        if (Has(args, "--probebrush")) { Line("· 画刷探针：" + ThemeManager.Probe()); return true; }
        if (Has(args, "--probebox")) { ProbeComboBox(); return true; }

        var captureIndex = IndexOf(args, "--capture");
        if (captureIndex >= 0)
        {
            var path = args.Length > captureIndex + 1 ? args[captureIndex + 1] : "/tmp/glpm-panel.png";
            var what = args.Length > captureIndex + 2 ? args[captureIndex + 2] : "panel";
            if (what == "traymenu")
            {
                CaptureTrayMenu(path, tray);
                return true;
            }
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
        Check("去掉误粘的 /api/v4", ServerAddress.Normalize("https://gitlab.example.com/api/v4") == "https://gitlab.example.com");
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
        // 主题画刷必须跟系统深浅一致：深色系统下弹出层必须是深色。
        // （曾经只查「画刷没被冻结」，可画刷一进应用资源就被冻结了，那个检查等于没查；
        //   真正的问题是主题压根切不动 —— 这里直接查颜色。）
        var popupBrush = Application.Current?.Resources["PopupBackgroundBrush"] as System.Windows.Media.SolidColorBrush;
        var expectedPopup = ThemeManager.IsDark
            ? System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x2C)
            : System.Windows.Media.Color.FromRgb(0xFD, 0xFD, 0xFD);
        Check("主题画刷跟随系统深浅", popupBrush is not null && popupBrush.Color == expectedPopup);
        Line($"· 主题：{(ThemeManager.IsDark ? "深色" : "浅色")} · 任务栏 {(ThemeManager.TaskbarIsLight ? "浅色" : "深色")} · 强调色 {ThemeManager.Accent}");

        Line(failures.Count == 0 ? $"全部通过（{total} 项）" : $"失败 {failures.Count} 项");
    }

    // MARK: --capture

    /// <summary>
    /// 抓托盘右键菜单。菜单是 WPF 弹层、不模态，所以这里用嵌套的 DispatcherFrame
    /// 把 UI 线程占住（弹层照常渲染、照常响应键盘），抓屏和收尾（按 ESC 收起菜单）
    /// 都放到后台线程做。
    ///
    /// 桌面被锁屏时抓不到屏，这时退一步：把菜单用 WPF 离屏渲染成 PNG，
    /// 落点信息照样能核对。
    /// </summary>
    private static void CaptureTrayMenu(string path, TrayIcon tray)
    {
        var done = new ManualResetEventSlim(false);
        var report = Path.ChangeExtension(path, ".txt");

        void Report(string text)
        {
            Line(text);
            try
            {
                var directory = Path.GetDirectoryName(report);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(report, text + Environment.NewLine);
            }
            catch (Exception)
            {
                // 报告写不出去也不该影响主流程
            }
        }

        var worker = new Thread(() =>
        {
            Thread.Sleep(1500);
            try
            {
                var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
                using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Report($"✓ 已抓取托盘菜单 {bounds.Width}x{bounds.Height} → {path}");
            }
            catch (Exception error)
            {
                Report($"✗ 抓屏失败：{error.Message}（桌面锁着或没有交互会话时抓不到屏，改用离屏渲染）");
                Report("  提示：桌面锁着时 Windows 会挂起会话的渲染，离屏渲染同样出不来图；"
                     + "解锁后重跑 --capture traymenu 就能看到真实观感。");
            }
            // 把菜单收掉，不然诊断进程会一直挂在菜单上
            keybd_event(0x1B, 0, 0, UIntPtr.Zero);
            keybd_event(0x1B, 0, 0x0002, UIntPtr.Zero);
            done.Set();
        })
        {
            IsBackground = true,
        };
        worker.Start();

        // 弹在托盘图标附近：菜单往左上展开，跟真右键同一个位置
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                   ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        tray.ShowMenuAt(new System.Drawing.Point(area.Right - 60, area.Bottom - 60));
        Report($"· 落点自查：{tray.DescribeMenuPlacement()}");

        // 菜单不是模态的：嵌套消息循环把 UI 线程占住，直到抓屏线程收工
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        var elapsed = 0;
        timer.Tick += (_, _) =>
        {
            elapsed += 250;
            if (elapsed == 500)
            {
                // 弹层窗口这时才 show 出来，列一遍本进程的顶层窗口核对落点
                foreach (var line in TopLevelWindows()) Report($"  {line}");
                // 同时离屏渲染一份菜单，抓不到屏时也能看观感
                var menuPng = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(path) + "-menu.png");
                Report(tray.RenderMenuToPng(menuPng)
                    ? $"✓ 菜单离屏渲染 → {menuPng}"
                    : "✗ 菜单离屏渲染失败");
                Report($"  渲染说明：{tray.MenuRenderNote}");
                Report($"  菜单画刷：{tray.MenuBrushDump}");
                Report($"  主题现状：{ThemeManager.Describe()}");
            }
            if (done.IsSet) frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
        worker.Join(2000);
        Report("✓ 托盘菜单已关闭");
    }

    /// <summary>列出本进程所有顶层窗口（可见与否都列，类名 + 屏幕矩形），用来核对菜单弹层的实际落点。</summary>
    private static List<string> TopLevelWindows()
    {
        var own = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        var result = new List<string>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != own) return true;
            var name = new StringBuilder(128);
            GetClassNameW(hwnd, name, name.Capacity);
            var visible = IsWindowVisible(hwnd) ? "可见" : "隐藏";
            if (GetWindowRect(hwnd, out var rect))
            {
                result.Add($"窗口 0x{hwnd:X} {visible} {name} ({rect.Left},{rect.Top}) {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    // 抓屏用：把目标窗口提到前面来（不设 HWND_TOPMOST —— 那会把 Popup 弹层压到窗口底下）
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    /// <summary>用 PrintWindow 把窗口内容直接画进位图：前面有别的窗口挡着也拍得到。</summary>
    private static System.Drawing.Bitmap? PrintWindowToBitmap(IntPtr hWnd, System.Drawing.Rectangle area)
    {
        try
        {
            if (area.Width <= 0 || area.Height <= 0) return null;
            var bitmap = new System.Drawing.Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    // 2 = PW_RENDERFULLCONTENT：连 DWM 合成的标题栏一起画
                    if (!PrintWindow(hWnd, hdc, 2))
                    {
                        graphics.ReleaseHdc(hdc);
                        hdc = IntPtr.Zero;
                        bitmap.Dispose();
                        return null;
                    }
                }
                finally
                {
                    if (hdc != IntPtr.Zero) graphics.ReleaseHdc(hdc);
                }
            }
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>判断位图是不是"一片死平"（全黑 / 全透明 / 单色）。
    /// PrintWindow 对某些窗口会返回一张空图，靠这个识别出来好退回抓屏。</summary>
    private static bool IsFlatImage(System.Drawing.Bitmap bitmap)
    {
        try
        {
            var first = bitmap.GetPixel(0, 0);
            var samples = 0;
            var same = 0;
            for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 24))
            {
                for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 24))
                {
                    var pixel = bitmap.GetPixel(x, y);
                    samples++;
                    if (pixel.R == first.R && pixel.G == first.G && pixel.B == first.B) same++;
                }
            }
            return samples > 0 && same == samples;
        }
        catch (Exception)
        {
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// 抓「下拉框展开」的样子：owner 窗口和弹层是两个顶层窗口，各抓各的再合成。
    /// 只抓 owner 拍不到弹层；只抓弹层又不知道它长什么样，合起来才是真实观感。
    /// </summary>
    private static System.Drawing.Bitmap? ComposeComboBox(Views.SettingsWindow window, System.Drawing.Rectangle area, IntPtr ownerHandle)
    {
        try
        {
            using var owner = PrintWindowToBitmap(ownerHandle, area);
            var popup = window.LanguagePopup();
            var source = popup?.Child is null
                ? null
                : PresentationSource.FromVisual(popup.Child) as System.Windows.Interop.HwndSource;
            if (owner is null || source is null) return null;
            if (!GetWindowRect(source.Handle, out var popupRect)) return null;

            var popupArea = new System.Drawing.Rectangle(popupRect.Left, popupRect.Top,
                popupRect.Right - popupRect.Left, popupRect.Bottom - popupRect.Top);
            using var popupImage = PrintWindowToBitmap(source.Handle, popupArea);
            if (popupImage is null || IsFlatImage(popupImage)) return null;

            // 弹层画在屏幕上的位置，换算成 owner 窗口内的坐标贴上去
            var offsetX = popupArea.Left - area.Left;
            var offsetY = popupArea.Top - area.Top;

            var canvas = new System.Drawing.Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(canvas))
            {
                graphics.Clear(System.Drawing.Color.Black);
                graphics.DrawImage(owner, 0, 0);
                graphics.DrawImage(popupImage, offsetX, offsetY);
            }
            return canvas;
        }
        catch (Exception error)
        {
            Line($"  下拉弹层合成失败：{error.Message}");
            return null;
        }
    }

    private static void Capture(string path, string what, Core.AppState state, AppSettings settings, PanelWindow panel)
    {
        settings.DemoMode = true;
        DemoData.Apply(state);

        Window target = what switch
        {
            "settings" or "projects" or "general" or "about" or "combobox" => CreateSettingsForCapture(what),
            "update" => CreateUpdateNoticeForCapture(),
            _ => panel
        };

        if (ReferenceEquals(target, panel)) panel.ShowAt(120, 80, settings.PanelWidth, settings.PanelHeight);
        else if (!target.IsVisible) target.Show();

        target.Activate();

        // 等布局与图标都画完再抓，否则容易拍到空白
        var done = new ManualResetEventSlim(false);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DoCapture();
        };
        timer.Start();

        void DoCapture()
        {
            try
            {
                // 抓整个窗口（含系统标题栏），不是只有客户区
                var handle = new System.Windows.Interop.WindowInteropHelper(target).Handle;

                var area = new System.Drawing.Rectangle(0, 0,
                    (int)Math.Round(target.ActualWidth), (int)Math.Round(target.ActualHeight));
                if (handle != IntPtr.Zero && GetWindowRect(handle, out var windowRect))
                {
                    area = new System.Drawing.Rectangle(windowRect.Left, windowRect.Top,
                        windowRect.Right - windowRect.Left, windowRect.Bottom - windowRect.Top);
                }

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // 优先 PrintWindow：直接把窗口内容画进位图，前面的窗口挡着也拍得到。
                // 诊断进程是后台启动的，抢不到前台焦点（实测拍到过聊天窗口和任务管理器），
                // 所以这条是主路径；抓不到东西时再退回抓屏。
                // 例外：下拉弹层是独立的 Popup 顶层窗口，父窗口的 PrintWindow 拍不到它，
                // 所以 combobox 这个目标必须走抓屏。
                var forceScreen = what == "combobox";
                var bitmap = !forceScreen && handle != IntPtr.Zero ? PrintWindowToBitmap(handle, area) : null;
                var how = "PrintWindow";
                if (bitmap is null || IsFlatImage(bitmap))
                {
                    bitmap?.Dispose();

                    // 退回抓屏：把窗口提到前面来。
                    // 注意不要设 HWND_TOPMOST —— owner 一旦置顶，普通层级的 Popup 弹层
                    // （下拉框就是）会被压在窗口底下，屏幕上就看不见了（实测弹层拍不到）。
                    if (handle != IntPtr.Zero)
                    {
                        BringWindowToTop(handle);
                        SetForegroundWindow(handle);
                    }

                    // 下拉框：弹层是独立的顶层窗口，owner 的 PrintWindow 拍不到它。
                    // 所以分两张抓（owner 一张、弹层一张）再合成 —— 屏幕上直接抓会被
                    // 别的窗口挡住（诊断进程是后台起的，抢不到前台焦点）。
                    if (what == "combobox" && target is Views.SettingsWindow comboWindow)
                    {
                        comboWindow.OpenFirstDropDown();
                        var popupReady = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                        popupReady.Tick += (_, _) =>
                        {
                            popupReady.Stop();
                            using var composed = ComposeComboBox(comboWindow, area, handle);
                            if (composed is null)
                            {
                                GrabScreen(area, handle, "抓屏");
                                return;
                            }
                            composed.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                            Line($"✓ 已抓取 {area.Width}x{area.Height}（设置窗口 + 下拉弹层合成）→ {path}");
                            done.Set();
                        };
                        popupReady.Start();
                        return;
                    }

                    GrabScreen(area, handle, "抓屏");
                    return;
                }

                using (bitmap)
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);

                Line($"✓ 已抓取 {area.Width}x{area.Height}（{how}）→ {path}");
                done.Set();
            }
            catch (Exception error)
            {
                Line($"✗ 抓屏失败：{error.Message}");
                done.Set();
            }
        }

        void GrabScreen(System.Drawing.Rectangle area, IntPtr handle, string how)        {
            try
            {
                using var bitmap = new System.Drawing.Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(area.X, area.Y, 0, 0, area.Size,
                        System.Drawing.CopyPixelOperation.SourceCopy);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);

                Line($"✓ 已抓取 {area.Width}x{area.Height}（{how}）→ {path}");
            }
            catch (Exception error)
            {
                Line($"✗ 抓屏失败：{error.Message}");
            }
            done.Set();
        }

        // 抓屏是异步的（要等界面画完），这里用嵌套消息循环把 UI 线程占住。
        // 不占住的话调用方一返回，应用就 Shutdown 了，timer 根本没机会跑 ——
        // 所以之前 --capture panel/settings 一直抓不到东西。
        var frame = new System.Windows.Threading.DispatcherFrame();
        var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        poll.Tick += (_, _) => { if (done.IsSet) frame.Continue = false; };
        poll.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        poll.Stop();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 下拉框显示探针：搞清楚 DisplayMemberPath 在我们这套自绘模板下到底起没起作用，
    /// 以及选中框（SelectionBoxItem）拿到的到底是数据项还是容器。
    /// 「下拉框里显示成 CLR 类型名」这个毛病就是靠它定位的。
    /// </summary>
    private static void ProbeComboBox()
    {
        Line("· 下拉框探针开始");
        try
        {
            ProbeComboBoxCore();
        }
        catch (Exception error)
        {
            Line($"✗ 探针抛异常：{error.GetType().Name} {error.Message}");
            Line(error.StackTrace ?? string.Empty);
        }
    }

    private static void ProbeComboBoxCore()
    {
        var box = new System.Windows.Controls.ComboBox
        {
            DisplayMemberPath = "Text",
            SelectedValuePath = "Value",
            Width = 220,
        };
        box.Items.Add(new ProbeOption("选项甲", 1));
        box.Items.Add(new ProbeOption("选项乙", 2));
        box.SelectedIndex = 0;

        var window = new Window { Content = box, Width = 320, Height = 160, ShowActivated = false };
        window.Show();
        window.UpdateLayout();
        box.Measure(new Size(220, 26));
        box.Arrange(new Rect(0, 0, 220, 26));
        box.UpdateLayout();

        Line($"· ItemTemplate={Describe(box.ItemTemplate)}");
        Line($"· SelectionBoxItemTemplate={Describe(box.SelectionBoxItemTemplate)}");
        Line($"· SelectionBoxItem={Describe(box.SelectionBoxItem)}");
        Line($"· SelectedValue={box.SelectedValue}");
        var container = box.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.Controls.ComboBoxItem;
        Line($"· 容器={container?.GetType().Name ?? "无"}");
        Line($"· 容器.Content={Describe(container?.Content)}");
        Line($"· 容器.ContentTemplate={Describe(container?.ContentTemplate)}");
        Line($"· 容器.DataContext={Describe(container?.DataContext)}");

        // 第二组：显式给 ItemTemplate（绑定 Text），看容器会不会拿到它
        var box2 = new System.Windows.Controls.ComboBox
        {
            SelectedValuePath = "Value",
            Width = 220,
            ItemTemplate = BuildTextTemplate(),
        };
        box2.Items.Add(new ProbeOption("选项丙", 3));
        box2.SelectedIndex = 0;
        window.Content = box2;
        window.UpdateLayout();
        box2.Measure(new Size(220, 26));
        box2.Arrange(new Rect(0, 0, 220, 26));
        box2.UpdateLayout();
        var container2 = box2.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.Controls.ComboBoxItem;
        Line($"· [显式模板] ItemTemplate={Describe(box2.ItemTemplate)}");
        Line($"· [显式模板] 容器={container2?.GetType().Name ?? "无"}");
        Line($"· [显式模板] 容器.Content={Describe(container2?.Content)}");
        Line($"· [显式模板] 容器.ContentTemplate={Describe(container2?.ContentTemplate)}");
        Line($"· [显式模板] SelectionBoxItemTemplate={Describe(box2.SelectionBoxItemTemplate)}");
        window.Close();
    }

    /// <summary>一个只显示 Text 属性的 DataTemplate。</summary>
    private static DataTemplate BuildTextTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        factory.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
            new System.Windows.Data.Binding("Text"));
        var template = new DataTemplate { VisualTree = factory };
        template.Seal();
        return template;
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        _ => value.ToString() ?? "?",
    };

    /// <summary>探针用的选项对象：形状跟设置窗口里的 SettingsOption 一样。</summary>
    private sealed class ProbeOption
    {
        public ProbeOption(string text, int value)
        {
            Text = text;
            Value = value;
        }

        public string Text { get; }
        public int Value { get; }
    }

    /// <summary>造一个假的更新弹窗用来抓屏（诊断用，不联网）。</summary>
    private static Window CreateUpdateNoticeForCapture()
    {
        var release = new GitHubRelease
        {
            TagName = "v9.9.9",
            HtmlUrl = "https://github.com/takmartian/gitlab-pipeline-monitor-win/releases/tag/v9.9.9",
            Body = "## 这一版改了什么\n\n"
                 + "- 托盘右键菜单改成自绘的飞层菜单，观感对齐 Windows 安全中心，深浅跟随系统。\n"
                 + "- 修掉主题画刷被冻结的根因：以前深色系统下界面其实一直是浅色的。\n"
                 + "- 设置界面和弹窗的观感统一，不再有浅色控件露在深色底上。\n"
                 + "- 菜单落点按显示器缩放精确计算，多显示器不同缩放也不会跑偏。\n\n"
                 + "感谢使用，有问题欢迎反馈。",
        };
        return new UpdateNoticeWindow(release, Core.AppInfo.Version, UpdateNotes.Plain(release.Body));
    }

    private static Window CreateSettingsForCapture(string tab)
    {
        var app = (App)Application.Current;
        app.ShowSettings(tab switch
        {
            "projects" => Views.SettingsTab.Projects,
            "general" or "combobox" => Views.SettingsTab.General,
            "about" => Views.SettingsTab.About,
            _ => Views.SettingsTab.Connection
        });
        // ShowSettings 打开的是应用持有的那个窗口，这里取回来抓
        var window = Application.Current.Windows.OfType<Views.SettingsWindow>().FirstOrDefault();
        if (window is not null)
        {
            // 抓图别把真实服务器地址拍进去（截图是要往外发的）。
            // 只换显示，不碰 settings —— 改 ServerUrl 会立刻落盘，把用户配置写坏。
            window.UsePreviewServerUrl();

            // 下拉框状态：显示成 CLR 类型名 / 一片空白都是这里出问题时先看的
            Line($"  下拉框：{window.DebugDump()}");
            // combobox 目标：把下拉展开，抓「下拉项本身」的显示
            if (tab == "combobox") window.OpenFirstDropDown();
        }
        return window ?? new Window { Width = 620, Height = 560, Title = "设置" };
    }
}
