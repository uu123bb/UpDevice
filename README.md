# UpDevice

> [!WARNING]
> 
> 本项目由 **DeepSeek V4.1 Flash** 完成

为 [sleepy](https://github.com/sleepy-project/sleepy) 上报设备信息的 Windows 客户端。

## 这是什么

[sleepy](https://github.com/sleepy-project/sleepy) 是一个用于展示个人在线状态的站点：访客可以看到你当前「在不在」「在用什么设备」「正在用什么软件」。这些信息不是服务端猜出来的，而是由客户端**主动推送**。

UpDevice 就是这个推送端在 Windows 上的实现 —— 后台采集本机设备的使用状态（是否在使用、当前前台应用），按 sleepy 的 Device API 定期上报。

## 组件

| 项目 | 角色 | 职责 |
| --- | --- | --- |
| `UpDevice.Service` | 主进程 | 采集前台窗口与应用信息，判断设备使用状态，按间隔调用 sleepy API 上报；承载全部核心逻辑 |
| `UpDevice.Tray` | 托盘程序 | 常驻托盘，并在本机起一个 Web 服务提供配置界面；控制主进程启停、展示上报状态 |

两个进程分离的原因：采集与上报需要长期驻留，配置与状态查看需要交互入口。托盘程序不承担采集逻辑，只做控制面。

### 主进程（UpDevice.Service）

名字叫 Service，但**它不是 Windows 服务（SCM）**，而是一个普通的桌面进程，只是没有可见界面：

- 在用户交互式会话内运行 —— 前台窗口、空闲状态这些信息只有在交互式会话里才拿得到，会话 0 的服务进程取到的永远是空。
- 持有一个**隐藏窗口**用于接收系统消息，例如关机/注销前的 `WM_QUERYENDSESSION` / `WM_ENDSESSION`，以便在退出前把状态收尾（上报离线，但保留设备）。
- 同时承担与托盘的 IPC：托盘通过窗口消息或命名管道通知它重载配置、暂停采集。

> 注意：隐藏窗口必须是**普通顶层窗口**（`ShowInTaskbar = false`），不能用消息专用窗口（`HWND_MESSAGE`）。消息专用窗口收不到 `WM_QUERYENDSESSION` 这类广播消息，用它就永远等不到关机通知。

启动方式：主进程不自己注册为服务，而是由**计划任务（用户登录时触发）**或一个只做拉起的 SCM 服务来启动。后者如果采用，服务本身只能当 launcher —— 需要在用户会话内 `CreateProcessAsUser` 拉起主进程，因为服务进程运行在会话 0。

### 托盘程序（UpDevice.Tray）

WinForms 实现，只有托盘图标与菜单（启停采集、打开配置页、退出），界面主体是下面这个本地 Web 服务。

#### 配置为什么走本地 Web 服务

托盘程序不写桌面表单，而是内嵌一个仅监听回环地址的 Web 服务，配置界面用浏览器打开。好处是不用为一个小配置页引入完整的桌面 UI 技术栈，表单校验与「测试连接」这类交互在网页里更容易实现，后续改配置项也不用重新设计布局。

```
浏览器 ──→ http://127.0.0.1:<port> ──→ UpDevice.Tray（Web 服务） ──→ 读写 config.json
```

该服务只绑定 `127.0.0.1`，不对局域网暴露；配置中的 `secret` 属于敏感信息，接口与页面均不做外网访问。

## 工作方式

```
   前台窗口 / 空闲状态 / config.json
                 │
                 ▼
        UpDevice.Service ────── 上报设备状态 ──────▶ sleepy
                 ▲
                 │ 读取 config.json
                 │
        UpDevice.Tray  ◀────── 浏览器访问 127.0.0.1:port 修改配置
```

## 与 sleepy 服务端的对接

sleepy 存在两套接口，本客户端会**自动识别**：先请求 `/query`，通得过就是 V4；否则再试 `/api/status/query`，通得过就是 V5。识别结果会缓存，中途发现 404 会自动换另一套重试。

| | V4（sleepy 4.x / serverless 分支） | V5（FastAPI 重构版） |
| --- | --- | --- |
| 查询 | `GET /query` | `GET /api/status/query` |
| 上报 | `POST /device/set` | `POST /api/device/set` |
| 移除 | `GET /device/remove` | `GET /api/device/remove` |
| 设备字段 | `app_name` | `status` |

> 注意：V4 **没有 `/api` 前缀，也没有 `/meta` 路由**。测试连接千万不要去打 `/api/meta`，那个接口不存在。

上报请求体会同时带 `app_name` 与 `status` 两个字段，服务端会忽略不认识的那个，这样一套代码两套接口都能用：

```jsonc
{
  "id": "device-1",        // 设备标识符
  "show_name": "MyDevice", // 设备显示名称
  "using": true,           // 是否正在使用
  "app_name": "VSCode",    // V4 字段
  "status": "VSCode"       // V5 字段
}
```

- **鉴权**：需与服务端 `SLEEPY_SECRET` 一致。支持 `Sleepy-Secret` 请求头、`Authorization: Bearer <secret>`、请求体 `secret` 字段、`?secret=` 查询参数等方式，本客户端同时带请求头与 Bearer。
- **测试连接的密钥校验**：故意发一个空请求体 `{}`。服务端先验密钥（错误 → 401）再校验参数（缺字段 → 400），所以拿到 400 就说明密钥正确，且不会写入任何设备数据。

## 技术栈

- 语言 / 运行时：C# / .NET 10（主进程与托盘为 `net10.0-windows10.0.19041.0`，共享库为 `net10.0`）
- 托盘程序：WinForms（`UseWindowsForms`）+ `NotifyIcon`
- 主进程隐藏窗口：WinForms `NativeWindow` 承载消息循环
- 配置界面：内嵌 ASP.NET Core Web 服务（Kestrel + 最小 API），仅绑定 `127.0.0.1`
- 计划任务：`schtasks`（`ONLOGON` + `HIGHEST`）
- 设备信息采集：前台窗口（`GetForegroundWindow` / `GetWindowThreadProcessId` / `GetClassNameW`）、空闲时间（`GetLastInputInfo`）、控制器（XInput `XInputGetState`）、电源（`GetSystemPowerStatus`）
- 媒体信息：WinRT SMTC（`GlobalSystemMediaTransportControlsSessionManager`），需要 `net10.0-windows10.0.19041.0` 目标框架
- 启动方式：计划任务（登录触发）拉起主进程

> 本项目的 SDK 版本与 ClassIsland-Misha 插件保持一致，便于共用同一套 .NET 10 工具链。

## 配置

配置内容由托盘程序维护，主进程只读取（文件缺失时按默认值创建，见下）。文件位于**两个程序所在的同一目录**下，名为 `config.json`：

```jsonc
{
  "version": 3,                        // 配置结构版本，旧版本会自动升级
  "reportingEnabled": true,            // 开始 / 停止上报
  "remote": {
    "serverUrl": "https://sleepy.example.com",
    "secret": "",
    "deviceId": "device-1",
    "showName": "MyDevice"
  },
  "report": {
    "intervalSeconds": 5,              // 上报间隔（秒）
    "onlyOnChange": true,              // 不重复上报：仅状态变化时发送
    "statusFormat": "window",          // process | window | window+process | process+window
    "smtcMode": "idle",                // off | idle | separate
    "idleTimeoutSeconds": 120,         // 0 表示禁用待机判定
    "controllerEnabled": false,
    "controllerAll": false,
    "controllerDeadzone": 0.10,
    "portableMode": false
  },
  "smtc": {                            // smtcMode = separate 时使用
    "deviceId": "",                    // 不能与本机 deviceId 相同
    "showName": "",
    "pendingRemoveId": ""              // 内部字段：待删除的旧 SMTC 设备
  },
  "update": {                          // v3 新增
    "enabled": true,                   // 自动检查更新
    "intervalHours": 6,                // 检查间隔（GitHub 匿名接口每小时 60 次）
    "repository": "uu123bb/UpDevice"   // 更新源
  },
  "texts": {
    "shutdown": "设备关闭",
    "batteryOnBattery": "未在充电 %battery%",
    "batteryOnAc": "正在充电 %battery%"
  }
}
```

`config.json` 中含 `secret`，**不纳入版本控制**，已加入 `.gitignore`。

### 配置版本升级

配置带 `version` 字段，加载时自动升级，**不会因为缺字段被当成损坏文件**：

- 没有 `version` 的旧文件按 v1 处理，补齐新字段后写回，日志会打印「从 v1 升级到 v2」。
- 每加一个字段就 `+1` 并补一段迁移：v2 加 SMTC 独立上报，v3 加自动更新。
- `version` 比程序还新时只提示、不回写 —— 免得降级运行时把用户的新配置冲掉。
- 只有 JSON 真的解析失败才会备份成 `config.json.invalid-<时间戳>` 并重建。

### 状态判定与上报规则

状态文案由「前缀 + 核心」组成，前缀是便携模式下的电池状态，核心随设备状态变化：

| 情形 | `using` | 核心内容 |
| --- | --- | --- |
| 有键鼠或控制器输入 | `true` | 按「上报形式」拼接的前台信息，控制器活跃时追加 `（正在使用控制器）` |
| 超过超时时间无输入 | `false` | `待机`；若 SMTC 正在播放且开启「仅在待机时上报」，则用播放信息替代 |
| 收到关机 / 注销信号 | `false` | 关机文案，整体用方括号包裹，**不删除设备** |

实际文案示例（`show_name` 由服务端拼在冒号前，不属于 `status`）：

```
workbuddy.exe
WorkBuddy
WorkBuddy（workbuddy.exe）
workbuddy.exe（WorkBuddy）
待机
泠鸢yousa - 一梦千霄（NetEase）
[未在充电 98%]WorkBuddy
[正在充电 50%]泠鸢yousa - 一梦千霄（NetEase）
[正在充电 50%]American Truck Simulator（正在使用控制器）
[设备关闭]
```

- 上报形式：`进程名`、`窗口名`、`窗口名（进程名）`、`进程名（窗口名）`，括号用全角。
- 进程名带扩展名（`workbuddy.exe`），取不到映像名时退回 `ProcessName + .exe`。
- SMTC 只认网易云音乐（`NetEase`）与 QQ 音乐（`QQ Music`），格式为 `歌手 - 歌曲（来源）`，无歌手时只显示歌曲。
- 关机与离线通过上报接口置 `using=false`，不调用 device/remove，设备仍留在页面上。

### 前台窗口的屏蔽规则

Windows 有一批临时浮层会抢走前台窗口：开始菜单、Alt+Tab、通知中心、输入法候选框、任务栏缩略图……它们不是用户真正在用的应用，直接上报会变成一串莫名其妙的窗口名。

命中屏蔽规则的窗口**不更新**当前前台信息，保留上一次有效结果。规则分三个维度（清单参照 sleepy 官方客户端 `client/win_device.py` 的 `SKIPPED_NAMES`，另补了进程名与类名）：

| 维度 | 例子 |
| --- | --- |
| 窗口标题 | 空标题、`任务切换`、`快速设置`、`通知中心`、`操作中心`、`搜索`、`任务视图`、`系统托盘溢出窗口。`、`Snipper - Snipaste` |
| 进程名 | `ShellExperienceHost.exe`、`StartMenuExperienceHost.exe`、`SearchHost.exe`、`TextInputHost.exe`、`LockApp.exe`、`Widgets.exe` |
| 窗口类名 | `Progman` / `WorkerW`（桌面）、`Shell_TrayWnd`（任务栏）、`NotifyIconOverflowWindow`（托盘溢出）、`XamlExplorerHostIslandWindow`（Win11 任务视图）、`IME`（输入法） |

没有把 `Windows.UI.Core.CoreWindow` 列入 —— UWP 应用本体也用这个类名，加进去会误杀正常应用。

同样的规则也影响「没有前台窗口」的判断：如果一直抓不到有效窗口（例如刚登录、只剩桌面），按待机处理。

### SMTC 单独上报成一个设备

`smtcMode = separate` 时，播放信息不再写进本设备，而是作为**第二个设备**独立上报，需要额外填 `smtc.deviceId` 与 `smtc.showName`。

| SMTC 状态 | 该设备 | 说明 |
| --- | --- | --- |
| 正在播放，且本机不在待机 | 在线 | `歌手 - 歌曲（NetEase）` |
| 暂停播放 | 离线 | 保留歌曲信息，不移除 |
| 本机判定为待机 | 离线 | 保留播放信息，不管是否在播 |
| 没有受支持的会话 | **删除设备** | 不挂一个「待机」的僵尸设备，会话重新出现时自动重建 |

- 两个设备各自去重、各自上报，互不干扰。
- `smtc.deviceId` 不能与本机 `deviceId` 相同，否则跳过
  并打一条警告（否则会把主设备覆盖掉）。
- 关闭该选项（或修改 `smtc.deviceId`）并保存时，托盘会把旧设备 ID 写进
  `smtc.pendingRemoveId`，由主进程调用 `device/remove` 删除，**删除成功才清除标记** ——
  因此主进程当时没运行也没关系，下次启动会补删。

### 配置文件由程序自举

`config.json` 不需要预置，也不由安装程序生成 —— **两个程序都遵循同一规则**：启动或读取配置时如果文件不存在（或不可读、解析失败），就按默认值创建一份再继续。

这样做的意义是把「谁先启动」的依赖彻底去掉：主进程先起来会创建它，托盘先起来也会创建它，两者不会因为对方没跑而卡住。写入需要幂等 —— 已存在时不覆盖用户配置，只在缺失或损坏时重建。

损坏的配置不会静默覆盖，会先重命名为 `config.json.invalid-<时间戳>` 再重建，避免手写配置时一个笔误就把数据冲掉。

因为不做安装程序，发布形态就是一个便携目录：两个可执行文件放在一起，`config.json` 首次运行时自动出现在旁边。

## 仓库结构

```
UpDevice/
├── src/
│   ├── UpDevice.Shared/           # 三个程序共用的部分
│   │   ├── Config/                # AppConfig、ConfigStore（自举 + 版本迁移 + 热重载）
│   │   ├── ConfigPath.cs          # config.json 定位（兼容扁平与启动器两种布局）
│   │   ├── AppVersion.cs          # 版本号读取与解析
│   │   ├── UpdateChecker.cs       # GitHub Release 检查
│   │   ├── SiblingProcess.cs      # 互相拉起
│   │   ├── SleepyApi.cs           # V4 / V5 接口差异
│   │   └── Logging.cs
│   ├── UpDevice.Service/          # 主进程：采集 + 上报 + 隐藏窗口
│   │   ├── Program.cs
│   │   ├── Native/                # Win32 封装、隐藏窗口
│   │   ├── Capture/               # 前台窗口、空闲、控制器、电池、屏蔽规则
│   │   ├── Media/                 # SMTC 播放信息
│   │   └── Reporting/             # sleepy 客户端与上报循环
│   └── UpDevice.Tray/             # 托盘 + 本地 Web 配置
│       ├── Program.cs             # 单实例、启动 Web 服务与托盘
│       ├── TrayContext.cs         # 托盘图标与菜单
│       ├── TrayWebHost.cs         # 回环 Web 服务与接口
│       ├── Ipc.cs                 # 向主进程发指令
│       ├── Autostart.cs           # 托盘开机自启（Run 键）
│       ├── ScheduledTask.cs       # 主进程计划任务（schtasks）
│       ├── Web/index.html         # 配置页
│       └── app.manifest           # requireAdministrator
├── UpDevice.sln
├── Directory.Build.props          # 统一版本号（CI 用 tag 覆盖）
├── .github/
│   ├── workflows/build.yml        # 构建、打包 ZIP、发版
│   └── release.yml                # Release 说明的分类规则
├── LICENSE
└── README.md
```

## 托盘程序

以 **管理员身份运行**（`app.manifest` 里 `requireAdministrator`）—— 创建与删除计划任务需要提权，与其每次操作弹 UAC，不如整体提权。

托盘菜单：

| 菜单项 | 行为 |
| --- | --- |
| 打开 Web 配置页面 | 用默认浏览器打开本地配置页 |
| 开机自动启动托盘（复选） | 写当前用户 `Run` 键，写入失败会把勾选状态弹回去 |
| 退出托盘 | 只退出托盘，主进程继续运行 |
| 退出托盘并停止上报 | 让主进程先上报 `[设备已停止上报]`，再连同主进程一起结束 |

两个程序**互相拉起**：主进程启动时发现托盘没在跑就拉起托盘，托盘启动时同样拉主进程。判断依据是各自的命名 Mutex，已经在运行就直接跳过，所以不会互相拉成死循环。只在启动时做一次，不做持续守护 —— 否则「退出托盘」会被立刻拽回来。

托盘整体提权意味着主进程若以普通权限启动、拉托盘时可能拿到 `ERROR_ELEVATION_REQUIRED`，这种情况只记录日志不弹窗；反过来托盘（已提权）拉主进程没有这个问题。


配置页由托盘内嵌的 Kestrel 提供，默认 `http://127.0.0.1:38945/`，端口被占用时自动向后找最多 10 个。**只绑回环地址**，因为它能读到 `secret`。

页面分两个选项卡：**配置**（上报相关全部选项）与 **自动更新**（开关、检查间隔、更新源、当前版本、立即检查）。

页面对应的接口：

| 接口 | 用途 |
| --- | --- |
| `GET /api/state` | 上报开关、任务计划、托盘自启、主进程是否在运行 |
| `GET /api/config` | 读取完整配置 |
| `POST /api/config` | 保存配置并通知主进程 `reload` |
| `POST /api/reporting` | 开始 / 停止上报 |
| `POST /api/task` | 启用 / 禁用计划任务 |
| `POST /api/test` | 探测远端实例（先 `/query`，再退到 `/api/status/query`），并可校验密钥 |
| `POST /api/update/check` | 查询 GitHub 最新 Release 并与当前版本比较（只查不装） |

计划任务用 `schtasks` 创建：`/SC ONLOGON /RL HIGHEST`，任务名 `UpDevice.Service`，指向同目录的 `UpDevice.Service.exe`。


## 构建与运行

需要 .NET 10 SDK：

```bash
dotnet build UpDevice.sln
```

调试用的两个开关：

```bash
# 采一轮就退出，打印前台窗口、空闲时长、电池、SMTC 与将要上报的内容
dotnet run --project src/UpDevice.Service -- --once

# 正常循环运行，但不真正发请求，只把要上报的内容写进日志
dotnet run --project src/UpDevice.Service -- --dry-run
```

日志写在程序目录下的 `logs/updevice-yyyyMMdd.log`，同时输出到控制台。

### 与托盘程序的指令通道

主进程持有一个隐藏窗口，标题固定为 `UpDevice.Service.Hidden`。托盘找到它后通过 `WM_COPYDATA` 发送指令：

| 指令 | 作用 |
| --- | --- |
| `reload` | 重新读取 config.json |
| `shutdown-report` | 立即上报离线（等同收到关机信号） |
| `stop-and-exit` | 上报 `[设备已停止上报]` 后退出主进程 |
| `exit` | 直接退出主进程 |

## 发布

不做安装程序，直接以便携目录分发：两个程序发布到同一目录即可，`config.json` 由程序首次运行时自动创建。

```bash
dotnet publish src/UpDevice.Service -c Release -o publish
dotnet publish src/UpDevice.Tray    -c Release -o publish
```

## 目录形态与配置位置

正式分发是一个「启动器 + 程序目录」的结构（`UpDevice.Launcher.exe` 尚未实现，规划中）：

```
UpDevice/
├── UpDevice.Launcher.exe     # 启动器：拉起托盘，将来负责自动更新
├── config.json               # 配置放在启动器旁边
└── UpDevice/                 # 程序目录，整包更新时会被替换
    ├── UpDevice.Service.exe
    └── UpDevice.Tray.exe
```

`config.json` **必须放在程序目录外面**：自动更新会整目录替换 `UpDevice/`，放在里面每次更新都会冲掉 server 和 secret。

程序定位配置的优先级（`UpDevice.Shared/ConfigPath.cs`）：

1. 环境变量 `UPDEVICE_CONFIG`（测试用）
2. 上一级目录存在 `UpDevice.Launcher.exe` → 用上一级的 `config.json`
3. 否则用程序自身目录的 `config.json`（开发时的扁平布局）

## 持续集成

`.github/workflows/build.yml`：

| 触发 | 行为 |
| --- | --- |
| Push 到 `main` | 编译 + 发布 + 上传 ZIP 产物（保留 14 天） |
| Pull Request 到 `main` | 同上，用于验证 PR |
| Push `v*` 标签 | 在构建基础上创建 Release，附上 ZIP，ChangeLog 自动生成 |
| 手动触发 | 可填版本号（如 `v1.0.0`）直接发版；留空只构建 |

打包细节：

- 以 `win-x64`、**self-contained** 方式发布，用户不需要预装 .NET 10 运行时。
- 两个程序发布到同一个 `publish/` 目录，一起压进 `UpDevice-<版本>-win-x64.zip`。
- 不生成 pdb（`-p:DebugType=none`），包内附 `README.md` 与 `LICENSE`。
- **版本号由 tag 注入**：`v1.0.1.1` → `-p:Version=1.0.1.1`；非 tag 构建标记为 `0.0.0-dev`。本地构建用 `Directory.Build.props` 里的值。
- 当前体积约 68 MB（压缩后）。

ChangeLog 用 GitHub 原生的 release notes 生成，分类规则写在 `.github/release.yml`（新功能 / 问题修复 / 文档与构建 / 其他）。

## 分支约定

- `main`：稳定分支，仅接受可发布的变更
- `dev`：日常开发分支，功能合入前先在此验证

## 提交规范

遵循 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/)，例如：

```
feat: 新增设备识别逻辑
fix: 修复空闲判定导致状态不更新的问题
docs: 更新使用说明
```

## 路线图

- [x] 建立解决方案骨架（`UpDevice.sln` + 主程序项目）
- [x] 前台窗口 / 窗口标题 / 窗口类名采集
- [x] 空闲状态判定（键鼠 + 控制器，超时转离线）
- [x] 控制器输入采集（XInput，死区可配）
- [x] 便携式设备模式（电池与电源状态）
- [x] SMTC 播放信息（网易云音乐 / QQ 音乐）
- [x] sleepy 上报客户端（鉴权、去重、关机离线）
- [x] `config.json` 自举与热重载
- [x] 主进程隐藏窗口（关机消息 + 指令通道）
- [x] 托盘程序：常驻、菜单、本地 Web 配置页
- [x] 计划任务开机自启（`/api/task` 启停）
- [x] 两程序互相拉起
- [x] 退出托盘并停止上报（上报 `[设备已停止上报]` 后一起结束）
- [x] GitHub Actions：构建、打包 ZIP、Release 与 ChangeLog
- [x] 自动更新：检查 GitHub 最新 Release（配置页「自动更新」选项卡）
- [ ] 启动器 `UpDevice.Launcher.exe`：拉起程序、下载并整包替换 `UpDevice/`
- [ ] 首次运行引导（检测到未配置时自动打开配置页）
- [ ] 计划任务的真实环境验证（沙箱内 `schtasks` 被禁用，仅验证了未就绪时的报错路径）

## 相关链接

- sleepy 主仓库：https://github.com/sleepy-project/sleepy
- sleepy 文档：https://sleepy.wss.moe
- sleepy API 文档：https://github.com/sleepy-project/sleepy/blob/main/doc/api.md

## 许可证

[MIT](./LICENSE)
