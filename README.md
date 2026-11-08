# UpDevice

[AI 写的原文档](https://github.com/uu123bb/UpDevice/blob/main/README_AI.md)

> [!WARNING]
>
> 项目由 *DeepSeek V4.1 Flash* 完成

向 [Sleepy](https://github.com/sleepy-project/sleepy) 上报设备信息的工具

## Sleepy 是什么？

Sleepy 是一个提供网页服务的 Flask 应用，可将站主所上传的设备当前状态~~公之于众~~

想要搭建 Sleepy？请参阅[这个项目](https://github.com/sleepy-project/sleepy)，或者你想在 *Vercel* 等无持久磁盘的平台上搭建的话，可以参阅[我的Fork](https://github.com/uu123bb/sleepy_serverless)（基于 V4，修改部分也是 Vibe Coding）

## 它的实现原理

### Sleepy 

**V4 和 V5 的接口名称有所不同，此处以 V4 为例**

Sleepy 的上报并不是真正意义上的上报，而是调用`/set`接口修改了某个`设备ID`下的当前状态信息

访问`/set`接口需要携带`SECRET`，修改设备信息也要带上`设备ID`

另外，访问`/query`接口可以获取到 JSON 形式的 Sleepy 信息，这也意味着你可以在能够显示 JSON 文本的浏览器中浏览

### 本项目

该项目作为**自动化工具**，获取当前的前台信息并构造信息，然后通过用户设置的`远端地址` `SECRET` `设备ID`将当前设备的状态上传到 Sleepy

## 技术栈

- 语言 / 运行时：C# / .NET 10（主进程与托盘为 `net10.0-windows10.0.19041.0`，共享库为 `net10.0`）
- 托盘程序：WinForms（`UseWindowsForms`）+ `NotifyIcon`
- 主进程隐藏窗口：WinForms `NativeWindow` 承载消息循环
- 配置界面：内嵌 ASP.NET Core Web 服务（Kestrel + 最小 API），仅绑定 `127.0.0.1`
- 计划任务：`schtasks`（`ONLOGON` + `HIGHEST`）
- 设备信息采集：前台窗口（`GetForegroundWindow` / `GetWindowThreadProcessId` / `GetClassNameW`）、空闲时间（`GetLastInputInfo`）、控制器（XInput `XInputGetState`）、电源（`GetSystemPowerStatus`）
- 媒体信息：WinRT SMTC（`GlobalSystemMediaTransportControlsSessionManager`），需要 `net10.0-windows10.0.19041.0` 目标框架
- 启动方式：计划任务（登录触发）拉起主进程（待修复：无法拉起）

## 计划

- [x] 基本完成上报部分
- [x] 实现控制器防休眠
- [x] 自动更新（使用启动器）
- [ ] 适配 [Fork](https://github.com/uu123bb/sleepy_serverless) 的心跳存活模式（待开发）
