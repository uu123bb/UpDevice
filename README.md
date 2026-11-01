# UpDevice

为 [sleepy](https://github.com/sleepy-project/sleepy) 上报设备信息的 Windows 客户端。

## 这是什么

[sleepy](https://github.com/sleepy-project/sleepy) 是一个用于展示个人在线状态的站点：访客可以看到你当前「在不在」「在用什么设备」「正在用什么软件」。这些信息不是服务端猜出来的，而是由客户端**主动推送**。

UpDevice 就是这个推送端在 Windows 上的实现 —— 后台采集本机设备的使用状态（是否在使用、当前前台应用），按 sleepy 的 Device API 定期上报。

## 组件

| 项目 | 角色 | 职责 |
| --- | --- | --- |
| `UpDevice.Service` | 主程序 / 后台服务 | 采集前台窗口与应用信息，判断设备使用状态，按间隔调用 sleepy API 上报；承载全部核心逻辑 |
| `UpDevice.Tray` | 托盘程序 | 常驻托盘，并在本机起一个 Web 服务提供配置界面；控制采集启停、展示上报状态 |

两个进程分离的原因：采集与上报需要长期驻留且不依赖用户会话，配置与状态查看需要交互入口。托盘程序不承担采集逻辑，只做控制面。

### 配置为什么走本地 Web 服务

托盘程序不写桌面表单，而是内嵌一个仅监听回环地址的 Web 服务，配置界面用浏览器打开。好处是不用为一个小配置页引入完整的桌面 UI 技术栈，表单校验与「测试连接」这类交互在网页里更容易实现，后续改配置项也不用重新设计布局。

```
浏览器 ──→ http://127.0.0.1:<port> ──→ UpDevice.Tray（Web 服务） ──→ 读写配置文件
```

该服务只绑定 `127.0.0.1`，不对局域网暴露；配置中的 `secret` 属于敏感信息，接口与页面均不做外网访问。

## 工作方式

```
   前台窗口 / 空闲状态 / 设备配置
                 │
                 ▼
        UpDevice.Service ────── POST /api/device/set ──────▶ sleepy
                 ▲
                 │ 读取配置
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

## 技术栈

- 语言 / 运行时：C# / .NET
- 托盘常驻：托盘图标宿主（`NotifyIcon`，宿主框架 WinForms / WPF 待定）
- 配置界面：内嵌 ASP.NET Core Web 服务（Kestrel + 最小 API），仅绑定 `127.0.0.1`
- 服务宿主：Windows 服务，或用户会话内自启进程（待定）
- 设备信息采集：Win32 前台窗口 API（`GetForegroundWindow` / `GetWindowThreadProcessId`）与系统空闲时间（`GetLastInputInfo`）

## 配置

配置由托盘程序维护，Service 读取。字段规划（名称待定）：

| 字段 | 说明 |
| --- | --- |
| `server_url` | sleepy 服务端地址，如 `https://sleepy.example.com` |
| `secret` | 与服务端 `SLEEPY_SECRET` 一致的鉴权密钥 |
| `device_id` | 设备标识符，对应 API 的 `id` |
| `show_name` | 页面显示的设备名称 |
| `interval` | 上报间隔 |

配置文件为 JSON，具体路径与读写方式待定（需保证 Service 与 Tray 两个进程都能访问）。

## 仓库结构（规划）

```
UpDevice/
├── src/
│   ├── UpDevice.Service/    # 采集与上报主程序
│   └── UpDevice.Tray/       # 托盘程序
├── UpDevice.sln
└── README.md
```

## 构建

需要 .NET SDK（版本待定）：

```bash
dotnet build UpDevice.sln
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
- [ ] 配置文件读写与共享（Service / Tray 共用）
- [ ] 托盘常驻与菜单（启停、打开配置页、退出）
- [ ] 本地 Web 配置服务（仅回环）+ 配置页与「测试连接」
- [ ] 开机自启 / 服务安装

## 相关链接

- sleepy 主仓库：https://github.com/sleepy-project/sleepy
- sleepy 文档：https://sleepy.wss.moe
- sleepy API 文档：https://github.com/sleepy-project/sleepy/blob/main/doc/api.md

## 许可证

待定。
