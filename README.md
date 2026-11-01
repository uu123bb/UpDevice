# UpDevice

为 [sleepy](https://github.com/sleepy-project/sleepy) 上报设备信息的 Windows 客户端。

## 这是什么

[sleepy](https://github.com/sleepy-project/sleepy) 是一个用于展示个人在线状态的站点：访客可以看到你当前「在不在」「在用什么设备」「正在用什么软件」。这些信息不是服务端猜出来的，而是由客户端**主动推送**。

UpDevice 就是这个推送端在 Windows 上的实现 —— 后台采集本机设备的使用状态（是否在使用、当前前台应用），按 sleepy 的 Device API 定期上报。

## 组件

| 项目 | 角色 | 职责 |
| --- | --- | --- |
| `UpDevice.Service` | 主程序 / 后台服务 | 采集前台窗口与应用信息，判断设备使用状态，按间隔调用 sleepy API 上报；承载全部核心逻辑 |
| `UpDevice.Tray` | 托盘程序 | 配置服务器地址、`secret`、设备 ID 与显示名；控制采集启停；展示最近一次上报结果与错误 |

两个进程分离的原因：采集与上报需要长期驻留且不依赖用户会话，配置与状态查看需要图形交互。托盘程序不承担采集逻辑，只做控制面。

## 工作方式

```
前台窗口变化 ──┐
空闲 / 输入状态 ├─→ 采集与判定 ──→ 组装上报体 ──→ POST /api/device/set ──→ sleepy
设备标识 / 配置 ┘                                    ▲
                                             UpDevice.Tray（配置 / 状态）
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
- 托盘程序：Windows 桌面 UI（WinForms 或 WPF）
- 服务宿主：Windows 服务，或用户会话内自启进程（待定）
- 设备信息采集：Win32 前台窗口 API（`GetForegroundWindow` / `GetWindowThreadProcessId`）与系统空闲时间（`GetLastInputInfo`）

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
- [ ] 配置持久化（服务器地址 / secret / 设备 ID）
- [ ] 托盘 UI：启停控制、状态与错误展示
- [ ] 开机自启 / 服务安装

## 相关链接

- sleepy 主仓库：https://github.com/sleepy-project/sleepy
- sleepy 文档：https://sleepy.wss.moe
- sleepy API 文档：https://github.com/sleepy-project/sleepy/blob/main/doc/api.md

## 许可证

待定。
