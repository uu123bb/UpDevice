# UpDevice

> [!CAUCTION]
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
- 持有一个**隐藏窗口**用于接收系统消息，例如关机/注销前的 `WM_QUERYENDSESSION` / `WM_ENDSESSION`，以便在退出前把状态收尾（如调用 `/api/device/remove` 下线本设备）。
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
        UpDevice.Service ────── POST /api/device/set ──────▶ sleepy
                 ▲
                 │ 读取 config.json
                 │
        UpDevice.Tray  ◀────── 浏览器访问 127.0.0.1:port 修改配置
```

## 与 sleepy 服务端的对接

上报接口（详见 [sleepy API 文档](https://github.com/sleepy-project/sleepy/blob/main/doc/api.md)）：

- **端点**：`POST /api/device/set`
- **请求体**：

```jsonc
{
  "id": "device-1",        // 设备标识符
  "show_name": "MyDevice", // 设备显示名称
  "using": true,           // 是否正在使用
  "status": "VSCode"       // 状态文本，通常是当前应用名
}
```

- **鉴权**：需与服务端 `SLEEPY_SECRET` 一致。支持 `Sleepy-Secret` 请求头、`Authorization: Bearer <secret>`、请求体 `secret` 字段等方式，本客户端使用请求头。

设备下线可用 `GET /api/device/remove?id=<device_id>`（需鉴权），在收到关机消息或用户退出时调用。

## 技术栈

- 语言 / 运行时：C# / .NET 10（`net10.0-windows`）
- 托盘程序：WinForms（`UseWindowsForms`）
- 主进程隐藏窗口：WinForms `NativeWindow` 承载消息循环
- 配置界面：内嵌 ASP.NET Core Web 服务（Kestrel + 最小 API），仅绑定 `127.0.0.1`
- 设备信息采集：Win32 前台窗口 API（`GetForegroundWindow` / `GetWindowThreadProcessId`）与系统空闲时间（`GetLastInputInfo`）
- 启动方式：计划任务（登录触发）拉起主进程

> 本项目的 SDK 版本与 ClassIsland-Misha 插件保持一致，便于共用同一套 .NET 10 工具链。

## 配置

配置内容由托盘程序维护，主进程只读取（文件缺失时按默认值创建，见下）。文件位于**两个程序所在的同一目录**下，名为 `config.json`：

```jsonc
{
  "server_url": "https://sleepy.example.com", // sleepy 服务端地址
  "secret": "",                               // 与服务端 SLEEPY_SECRET 一致
  "device_id": "device-1",                    // 对应 API 的 id
  "show_name": "MyDevice",                    // 页面显示的设备名称
  "interval": 30                              // 上报间隔（秒）
}
```

字段名称以实际实现为准。`config.json` 中含 `secret`，**不纳入版本控制**，已加入 `.gitignore`。

### 配置文件由程序自举

`config.json` 不需要预置，也不由安装程序生成 —— **两个程序都遵循同一规则**：启动或读取配置时如果文件不存在（或不可读、解析失败），就按默认值创建一份再继续。

这样做的意义是把「谁先启动」的依赖彻底去掉：主进程先起来会创建它，托盘先起来也会创建它，两者不会因为对方没跑而卡住。写入需要幂等 —— 已存在时不覆盖用户配置，只在缺失或损坏时重建。

因为不做安装程序，发布形态就是一个便携目录：两个可执行文件放在一起，`config.json` 首次运行时自动出现在旁边。

## 仓库结构（规划）

```
UpDevice/
├── src/
│   ├── UpDevice.Service/       # 主进程：采集 + 上报 + 隐藏窗口
│   │   ├── Program.cs
│   │   ├── Native/             # 隐藏窗口、系统消息、Win32 封装
│   │   ├── Capture/            # 前台窗口与空闲状态采集
│   │   └── Reporting/          # sleepy API 客户端
│   └── UpDevice.Tray/          # 托盘 + 本地 Web 配置
│       ├── Program.cs
│       ├── Web/                # 最小 API 与配置页
│       └── Ipc/                # 与主进程通信
├── UpDevice.sln
├── LICENSE
└── README.md
```

## 构建

需要 .NET 10 SDK：

```bash
dotnet build UpDevice.sln
```

## 发布

不做安装程序，直接以便携目录分发：两个程序发布到同一目录即可，`config.json` 由程序首次运行时自动创建。

```bash
dotnet publish src/UpDevice.Service -c Release -o publish
dotnet publish src/UpDevice.Tray    -c Release -o publish
```

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

- [ ] 建立解决方案骨架（`UpDevice.sln` + 两个项目）
- [ ] 前台窗口与应用名采集
- [ ] 空闲状态判定（是否正在使用）
- [ ] sleepy 上报客户端（含鉴权与重试）
- [ ] `config.json` 自举（缺失时创建默认配置）、读写与热重载
- [ ] 主进程隐藏窗口：接收关机消息与 IPC
- [ ] 托盘常驻与菜单（启停、打开配置页、退出）
- [ ] 本地 Web 配置服务（仅回环）+ 配置页与「测试连接」
- [ ] 计划任务开机自启

## 相关链接

- sleepy 主仓库：https://github.com/sleepy-project/sleepy
- sleepy 文档：https://sleepy.wss.moe
- sleepy API 文档：https://github.com/sleepy-project/sleepy/blob/main/doc/api.md

## 许可证

[MIT](./LICENSE)
