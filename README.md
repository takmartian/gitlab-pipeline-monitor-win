# GitLab Pipeline Monitor for Windows

原生 Windows 菜单栏（系统托盘）应用：在托盘看 GitLab 打星项目的流水线状态。
**WPF + Win32 手写，没有 Electron / Tauri / 任何第三方依赖。**

这是 [macOS 版](https://github.com/takmartian/gitlab-pipeline-monitor) 的 Windows 对应版本，
功能对齐、界面按 Windows 原生习惯重做，两端共用同一套设计（包括令牌加密方案和更新机制）。

## 下载

预构建包在 [Releases](https://github.com/takmartian/gitlab-pipeline-monitor-win/releases/latest)，
解压后双击 `GitLabPipelineMonitor.exe` 即可运行。

程序是**框架依赖**的，需要 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
（微软官方运行时，几百 MB 的程序里只占几 MB 的包体积）。
如果机器上没装，双击 exe 时 Windows 会自己弹框提示并带你去下载，点几下就装好了。
已经装了 .NET 8 桌面运行时的机器（很多开发机和装过 Visual Studio / Office 的机器都有）直接就能跑。

exe 未做代码签名，首次运行 Windows SmartScreen 可能提示「未知发布者」，
点「更多信息 → 仍要运行」即可（只影响第一次）。

## 功能

- **托盘图标**：图标随整体状态变色，可选状态圆点与失败数量徽章；左键开合面板，右键出菜单。
- **点图标即看面板**：按项目分组列出打星项目的最新流水线 —— 分支、提交、耗时、覆盖率、触发人、相对时间、状态色。
- **筛选与搜索**：全部 / 运行中 / 失败 / 成功 / 其他（带计数），搜索匹配项目、分支、SHA。
- **流水线操作**：重跑、取消；点进详情页看阶段与作业列表，可重跑 / 取消单个作业、打开作业日志。
- **运行新流水线**：选分支后触发（分支列表来自 GitLab API，默认选中默认分支，可搜索）。
- **设置**：服务器地址 + 令牌（个人访问令牌 / OAuth 两种头），测试连接（返回用户、GitLab 版本、项目数），
  重新拉取项目列表，仅监控星标 / 手动勾选项目，每个项目显示的流水线数。
- **令牌加密存本机**（AES-GCM），按服务器主机名分账户，切换实例互不覆盖，不落配置文件。
- **自动刷新**：从不 / 30s / 1 / 2 / 5 / 10 分钟；打开面板时可自动刷新；右键图标可暂停。
- **系统通知**：流水线失败、失败后恢复时通知（只在状态真的变化时通知，不会每次刷新都弹）。
- **自动更新**：直接以 GitHub Release 作为更新源：启动后每天查一次，发现新版本弹窗展示更新内容，
  你选「立即更新」或「忽略此版本」。通用设置里可以关掉自动检查。
- **面板可调大小**：宽度在设置里选；高度直接**拖面板底边**调整，双击底边恢复默认。高度会记住。
- **其他**：开机自启（注册表 Run 键）、中英文（跟随系统或手动）、改动即时生效无需保存、
  点击面板外部自动收起。
- **深浅配色跟随系统**：面板、托盘菜单、设置界面、更新弹窗共用一套深色配色与圆角观感，
  标题栏也是深色的；系统切浅色时整套跟着切。

## 使用

1. 双击 `GitLabPipelineMonitor.exe`，屏幕右下角托盘会出现图标（可能被折叠进「显示隐藏的图标」里）。
2. 左键点图标打开面板，点齿轮进设置。
3. 填服务器地址（如 `https://gitlab.example.com`，误带 `/api/v4` 会自动去掉）和令牌。
   令牌需要 `read_api` 权限；要读取你没加入的项目再加 `read_user`。
4. 点「测试连接」确认，然后「重新拉取项目列表」。
5. 之后点托盘图标就能看流水线；默认监控你打过星的项目。

## 令牌为什么不放凭据管理器

Windows 凭据管理器 / DPAPI 在 .NET 里是独立的 NuGet 包，会破掉「零第三方依赖」这条底线。
所以和 macOS 版一样用**加密文件 + 机器指纹派生密钥**：

- 文件：`%APPDATA%\GitLab Pipeline Monitor\tokens.json`
- 加密：AES-GCM（.NET 内置）
- 密钥 = SHA256(固定前缀 + 本机 MachineGuid + 当前用户名 + 每条记录独立随机 salt)，密钥本身不落盘

所以密文拷到别的机器或别的账户下都解不开。按服务器主机名分账户，多台 GitLab 实例互不覆盖。

## 自动更新

更新源就是本仓库的 GitHub Release，不需要额外的服务器或对象存储：

1. 启动后延迟几秒查一次 `releases/latest`，之后每 6 小时看一眼（本地缓存 24 小时，不会碰限流）；
2. 版本号按数字分段比较（`1.1.10` 比 `1.1.9` 新），只在比当前版本新时才弹窗；
   弹窗里展示更新内容，两个按钮：「立即更新」开始下载安装，「忽略此版本」记下这个版本号
   （比它新的版本照样提示），通用设置里可以「取消忽略」；
3. 更新时下载 release 里的 zip 资产，用 GitHub 在资产上给出的 `sha256` 摘要校验完整性；
4. Windows 上不能删除正在运行的 exe，但可以重命名它 —— 所以由一个独立的 PowerShell 进程
   等本进程退出 → 把旧 exe 改名 → 拷入新文件 → 启动新版本 → 删掉旧包；
5. 全程零第三方依赖，不引 Sparkle / NetSparkle 之类的更新框架。

> **国内直连 GitHub 慢**：自包含包 100MB+，实测下载可能要几十秒到几分钟。
> App 走系统代理设置；如果你的网络需要代理，先把系统代理配上再更新。

## 构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。在 macOS / Linux / Windows 上都能构建：

```bash
./build.sh                  # 发布 win-x64 框架依赖版并打包 zip（默认）
./build.sh --self-contained # 需要把 .NET 一起打包时才用（包会从几 MB 变成 100MB+）
./build.sh debug            # 调试构建
dotnet run --project tests/GitLabPipelineMonitor.Tests   # 跑逻辑测试（零依赖自研 runner，可在任意平台跑）
```

> 测试用的是自研 runner（不是 xUnit），所以要用 `dotnet run --project tests/...` 跑，
> `dotnet test` 对这个项目无效。

项目分三层，`Core` 不依赖任何 Windows 专有 API，所以全部业务逻辑都能在 macOS 上编译和测试：

```
src/GitLabPipelineMonitor.Core/    net8.0        数据、API、加密、设置、状态编排、本地化、格式化
src/GitLabPipelineMonitor/         net8.0-windows  WPF 界面 + Win32 托盘 + 更新器 + 诊断
tests/                             net8.0        零依赖测试 runner
```

### 诊断参数

```bash
GitLabPipelineMonitor.exe --demo                 # 演示数据填充并打开面板（不联网也能看界面）
GitLabPipelineMonitor.exe --updateprobe          # 打印 GitHub Release 最新版本 / 资产 / sha256
GitLabPipelineMonitor.exe --updateui             # 回归探针：开着设置窗口跑一次更新检查（查跨线程异常）
GitLabPipelineMonitor.exe --updateinstall        # 端到端验证下载 → 校验 → 解压链路
GitLabPipelineMonitor.exe --selftest             # 关键逻辑自检
GitLabPipelineMonitor.exe --probebrush           # 主题画刷的可变性探针（见「托盘菜单为什么是自绘的」）
GitLabPipelineMonitor.exe --probebox             # ComboBox 显示机制探针（为什么不能靠 DisplayMemberPath）
GitLabPipelineMonitor.exe --capture out.png panel # 弹出真实界面后抓屏
GitLabPipelineMonitor.exe --capture out.png traymenu # 弹出托盘右键菜单后抓屏
```

`--capture` 的目标：`panel` 面板 / `settings` 设置（连接页）/ `general` 设置（通用页）/
`projects` 设置（项目页）/ `about` 关于页 / `update` 更新弹窗 / `combobox` 展开的下拉框 /
`traymenu` 托盘右键菜单。

> 抓屏走 `PrintWindow`（直接把窗口内容画进位图），所以前面挡着别的窗口也拍得到 ——
> 诊断进程是后台启动的，抢不到前台焦点，直接截屏会拍到聊天窗口之类的东西。
> 下拉框的弹层是独立的顶层窗口，父窗口的 `PrintWindow` 拍不到它，
> 所以那一项是「窗口一张 + 弹层一张」合成出来的。

> 抓屏需要桌面处于解锁状态：锁屏时 Windows 挂起会话渲染，既抓不到屏，
> WPF 的离屏渲染也出不来图（诊断里会自动退化为离屏渲染并说明原因）。

## 与 macOS 版的差异

| | macOS 版 | Windows 版 |
|---|---|---|
| 界面 | SwiftUI + AppKit | WPF |
| 菜单栏 / 托盘 | NSStatusItem + NSPopover | NotifyIcon + 自绘飞层菜单 + 无边框窗口 |
| 通知 | UserNotifications | 托盘气泡 |
| 开机自启 | SMAppService | 注册表 Run 键 |
| 机器指纹 | IOPlatformUUID | MachineGuid |
| 更新替换 | 独立 helper 脚本 | 独立 PowerShell 进程 |

令牌加密方案、更新源、界面信息层次、中英文文案、状态色与图标语义两端一致。

## 托盘菜单为什么是自绘的

托盘右键菜单不是系统原生菜单（HMENU），而是自己用 WPF 画的一层飞层弹层，
观感对齐 Windows 安全中心那个菜单：深灰底、圆角 8、柔和投影、菜单项平铺不带分隔线、
行高 40、字号 14、悬停是一块圆角浅色。系统是深色时菜单就是深色。

**为什么不直接用原生菜单**：Win11 25H2 把 Win32 深色主题的开关
（`SetPreferredAppMode` / `AllowDarkModeForWindows` / `RefreshImmersiveColorPolicyState`）
从 `uxtheme.dll` 里删干净了 —— 本机只剩 85 个导出，这几个入口一个都不剩。
于是原生菜单在任何主题下都只能是浅色：给归属窗口或菜单窗口打沉浸式深色标记无效，
套 `SetWindowTheme("DarkMode_Explorer")` 也无效（菜单绘制压根不走 uxtheme）。
微软自己的 MMC 在同一台机器上菜单同样是白的 —— 安全中心那种深色菜单是 WinUI(XAML) 画的，
不是原生菜单。要同样的观感，只能自己画。

**怎么落位**：一个 1×1 全透明的锚点窗口摆在鼠标（也就是托盘图标）上，
菜单用 `Placement=Custom` 定位：右缘贴着鼠标右侧 6px、下缘在鼠标上方 6px，
再夹回所在显示器的工作区。位置全程用物理像素算完，再按锚点所在显示器的缩放折回 DIP，
多显示器不同缩放也不会跑偏。

**顺带修掉的一个老问题**：主题画刷一旦放进 `Application.Resources` 就会被 WPF 立刻冻结，
改不动颜色 —— 结果是应用一直停在浅色配色上，深色系统下菜单也跟着是白的。
现在改成「换主题时整份重建字典再换进 `MergedDictionaries`」，`DynamicResource` 会重新解析，
深浅切换即时生效。`--probebrush` 可以现场验证这套行为。

## 设置界面与更新弹窗

设置窗口和更新弹窗跟托盘菜单是同一套观感：深色底、圆角、同一套间距和强调色，
标题栏也跟着系统走深色（DWM `DWMWA_USE_IMMERSIVE_DARK_MODE`，19/20 两个属性都写，
兼容不同版本的 Windows），切换主题时重刷。

几个实现上的坑：

- **标题栏颜色要自己接管**：WPF 自带的深色标题栏只有 `WindowChrome` 配合才有，
  默认标题栏永远是白底黑字。这里用 DWM 属性让系统给标题栏上深色。
- **强调色上的前景色要自适应**：本机强调色是很亮的黄（`#FFD3CB47`），
  白勾白字压不住，所以勾选框的对勾、强调按钮上的文字都按强调色亮度自动选深色或浅色。
- **ComboBox 不靠 `DisplayMemberPath`**：这个属性在「直接 `Items.Add`」而不是 `ItemsSource`
  的用法下根本不会生成模板，结果选中框里显示的是 CLR 类型名（`SettingsOption`）。
  现在显式给 `ItemTemplate`，选中框直接绑 `SelectedItem.Text`。
  `--probebox` 可以现场看到模板有没有生成。
- **更新弹窗正文顶对齐**：隐式 TextBox 样式是垂直居中，正文会浮在中间，
  这次改成 `VerticalContentAlignment="Top"`。
- **占位文字会盖在真值上**：占位提示的显隐读的是输入框的 `Text`，
  而构造函数里绑定还没把已保存的地址灌进来，只在那儿算一次的话提示会一直亮着、
  和真实地址叠成两行。现在改成跟着 `TextChanged` 走，绑定推值时也会收起。
- **图标要用 `Viewbox` 缩放**：图标几何都画在 24×24 视图里，
  直接把 `Path` 塞进固定尺寸的 `Grid` 是不行的 —— `Grid` 没定义列时隐式列会按
  几何的原始尺寸（星形是 20×19）撑开，`Path` 就按原尺寸绘制、溢出后被祖先裁掉，
  五角星只剩左上角一块，看着像个对勾。要么套 `Viewbox`（本项目统一这么做），
  要么给 `Path` 写死 `Width`/`Height` 加 `Stretch="Uniform"`。
- **图标要按最终尺寸设计，不能只画好看再缩小**：几何画在 24×24 视图里，
  但流水线行首那些元信息图标只有 8 DIP（200% 缩放下也就 16 设备像素）。
  细线画的图形缩到这个尺寸会散架：原来的分支图标是三个圆点加细曲线，
  8 DIP 下就是三颗散点；空心星的内圈挖空星跟外圈几乎一样大，星体被啃得只剩几根细条，
  12 DIP 下看着像个畸形带疙瘩的星。所以这些图标改成了实心剪影 ——
  分支=实心 Y 叉、手动=加粗手指、覆盖率=盾牌+对勾、提交 SHA=圆环、立方体=三个独立面，
  时钟指针和告警感叹号也加粗。
  另外凡是「外圈 + 内圈挖空」的图形一律带 `F0`（EvenOdd 填充规则），
  不然两个子路径的绕向一变，洞就消失了。
  想核对某个图标在目标尺寸下长什么样，可以拿 `artifacts/iconprobe` 那个探针
  把整套几何按 8/10/12/14/18/24 DIP 渲染成对照表。
- **WPF 的 `<Image Source="xxx.ico">` 只解出第一帧**：`app.ico` 里存了
  16/24/32/48/64/128/256 七档，256 那档本身是清晰的，但 WPF 加载 `.ico` 时
  只取第一帧（16×16），再拉到 72 DIP 放大 —— 「关于」页的应用 logo 就这么糊成了马赛克。
  凡是放大显示的位图都不能靠 `.ico`，要么自己挑帧，要么直接上矢量。
  现在「关于」页和更新弹窗的应用图标改用 `Theme/Logo.xaml` 里的 `DrawingImage`
  （按 `app.ico` 的 256×256 帧比例重画：圆角方块底座 + 三个节点 + 两根斜杆），
  任意尺寸都清晰。`app.ico` 本体不动 —— exe 图标和托盘兜底还在用它。
- **顶栏/底栏不要铺灰底**：面板、详情页、运行页的顶栏和底栏原来用
  `BarBackgroundBrush`（深色 `#272727`、浅色 `#F3F3F3`）铺了一层，
  又被 `Margin` 内缩成一块**带直角边的浮空灰块**，看着像没画完的占位块。
  现在直接用面板底色，不加分隔线。

## 线程约定：状态在后台线程，界面在 UI 线程

Core（`AppState` / `AppSettings`）跑在后台线程上：网络请求在异步方法里，
状态一改就抛 `PropertyChanged`。而订阅这些事件的界面代码会去动
`ObservableCollection`、`TextBox`、依赖属性 —— 这些都**只能在 UI 线程上做**。
硬来会得到两种报错：

- 「调用线程无法访问此对象，因为另一个线程拥有该对象」
- 「该类型的 CollectionView 不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改」

所以规矩有三条：

1. **Core 那边不用管线程**：它只管改状态、抛通知。
2. **界面那边统一用 `UiThread.Run(...)` 搬运**（`src/GitLabPipelineMonitor/UiThread.cs`）。
   面板的分组列表、设置窗口的项目行、详情页和运行页的按钮状态都走这一条路。
3. **`UpdateManager` 的状态只能在 UI 线程上改**：`Phase` 和下载进度会直接绑到
   设置窗口的界面上，所以 `CheckAsync` / `InstallAsync` 里的 `await` **不能加
   `ConfigureAwait(false)`** —— 加了续体就留在后台线程，改 `Phase` 时订阅者一动
   界面元素就炸。

`--updateui` 是这条约定的回归探针：开着设置窗口跑一次强制更新检查，
复现条件与当初的崩溃完全一致。

`--updateprobe` 踩的是同一个坑的另一面：诊断跑在 UI 线程上，
`await` 的续体要回 Dispatcher 执行，所以**不能**用 `GetAwaiter().GetResult()` 干等
（UI 线程一卡，续体永远等不到，症状是只打印两行然后进程挂死）。
`Diagnostics.RunDiagnostic` 用嵌套的 `DispatcherFrame` 边等边转消息循环。

## 别把真实信息写进仓库

这个仓库是公开的，提交上去的东西就等于公开了。三条红线：

- **演示数据**（`DemoData.cs`）里的项目路径、用户名、分支名**全是编造的**。
  照抄真实仓库的名字就等于把内部项目结构和员工账号公开了 ——
  要加演示数据请用 `web/customer-admin`、`demo.user` 这种一眼假的值。
- **本机配置和令牌**不进仓库：它们在 `%APPDATA%\GitLab Pipeline Monitor\` 下
  （`settings.json` 明文存服务器地址，`tokens.json` 是 AES-GCM 密文）。
  `.gitignore` 里也挡了一层（`settings.json`、`tokens.json`、`.env` 等），
  但真正的防线是别把这些文件拷进仓库目录。
- **诊断截图**（`--capture`）会把服务器地址换成 `gitlab.example.com` 再拍，
  免得截图往外发的时候带出真实地址。

## 许可

MIT
