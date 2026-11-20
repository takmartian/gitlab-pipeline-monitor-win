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
dotnet test                 # 跑逻辑测试（零依赖自研 runner，可在任意平台跑）
```

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
GitLabPipelineMonitor.exe --updateinstall        # 端到端验证下载 → 校验 → 解压链路
GitLabPipelineMonitor.exe --selftest             # 关键逻辑自检
GitLabPipelineMonitor.exe --capture out.png panel # 弹出真实界面后抓屏
```

## 与 macOS 版的差异

| | macOS 版 | Windows 版 |
|---|---|---|
| 界面 | SwiftUI + AppKit | WPF |
| 菜单栏 / 托盘 | NSStatusItem + NSPopover | NotifyIcon + 无边框窗口 |
| 通知 | UserNotifications | 托盘气泡 |
| 开机自启 | SMAppService | 注册表 Run 键 |
| 机器指纹 | IOPlatformUUID | MachineGuid |
| 更新替换 | 独立 helper 脚本 | 独立 PowerShell 进程 |

令牌加密方案、更新源、界面信息层次、中英文文案、状态色与图标语义两端一致。

## 许可

MIT
