# 代码签名政策 / Code signing policy

[English version below](#english)

---

## 中文

### 签名范围

本项目为 Windows 发布构建签名，覆盖 zip 包内的全部程序文件：

| 文件 | 说明 |
| --- | --- |
| `UpDevice.Launcher.exe` | 启动器，zip 根目录唯一的可执行文件 |
| `app-<版本>-<序号>/UpDevice.Service.exe` | 主进程（采集与上报） |
| `app-<版本>-<序号>/UpDevice.Tray.exe` | 托盘程序与本地配置页 |
| `app-<版本>-<序号>/Telemetry/UpDevice.Telemetry.dll` | ATS / ETS2 游戏内遥测插件 |

不含签名的是第三方运行库与 .NET 运行时文件（它们由 Microsoft 或各自上游签名）。

### 免费代码签名来源

本项目是采用 [MIT 许可证](LICENSE) 的开源项目，计划通过 **SignPath Foundation** 申请面向开源项目的免费代码签名：

- 证书由 SignPath Foundation 持有，私钥存放在 SignPath 的 HSM 中，本仓库任何成员都无法导出。
- 每次发版由 SignPath 校验「产物确实由本仓库的 GitHub Actions 构建」后再签名。
- 服务地址：<https://signpath.org/>

> 状态说明：申请与审批流程进行中。**此前发布的版本没有受信任的代码签名**，
> 首次下载运行时 Windows 可能提示「Windows 已保护你的电脑」。
> 解决方式见文末「首次运行被拦」。

### 团队角色

项目目前由一名维护者负责：

| 角色 | 人员 | 职责 |
| --- | --- | --- |
| 作者 / Author | [@uu123bb](https://github.com/uu123bb) | 编写代码、发起发版 |
| 审核 / Reviewer | 同上 | 复核提交与发版内容 |
| 批准 / Approver | 同上 | 在 SignPath 上人工批准每次签名请求 |

所有参与者的 GitHub 与 SignPath 账号均启用多因素认证（MFA）。

### 隐私

本程序**不收集、不上传**任何与第三方相关的个人信息。它会读写的内容只有：

- 你自行配置的远端地址、`SECRET`、设备 ID 与设备名（保存在本地 `config.json`）；
- 当前前台窗口的进程名/窗口标题、空闲时长、电源状态、系统正在播放的媒体信息、控制器输入；
- 启用「公路与车辆」时，游戏内的遥测数值（车速、油量、任务等）。

以上信息只发送到你配置的 Sleepy 服务端，不发送给本项目作者或任何第三方。
详细说明见 [README_AI.md](README_AI.md)。

### 对系统的改动

程序不做静默安装，全部改动都可见、可撤销：

| 改动 | 位置 | 何时发生 |
| --- | --- | --- |
| 生成配置文件 | `data/config.json`（或程序目录） | 首次运行 |
| 运行日志 | `data/Logs/` | 运行时 |
| 开机自启（托盘） | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | 你在设置页开启时 |
| 登录时启动托盘 | 计划任务 `UpDevice.Tray`（`schtasks`，`ONLOGON` + `HIGHEST`） | 你启用「开机启动（任务计划）」时 |
| 遥测插件 | `<游戏目录>\bin\win_x64\plugins\UpDevice.Telemetry.dll` | 你在设置页点「安装」时 |

程序不注册 Windows 服务，不写 `Program Files` 之外的系统目录，不安装驱动。

### 卸载

1. 托盘菜单里退出程序；
2. 删除程序目录（含 `data/`）；
3. 按需清理：设置页关掉「开机启动」会自动删除 Run 键与计划任务；「遥测插件管理」里点「卸载」会删除游戏目录里的插件 DLL。

### 如何验证签名

```powershell
# 单个文件
Get-AuthenticodeSignature .\UpDevice.Launcher.exe | Format-List Status, SignerCertificate

# 或
signtool verify /pa /v .\UpDevice.Launcher.exe
```

### 首次运行被拦

签名生效前（或使用自签名构建时）可能遇到 SmartScreen 拦截：

```powershell
# 解除单个文件的「下载标记」
Unblock-File .\UpDevice.Launcher.exe
```

或右键文件 → 属性 → 勾选「解除锁定」。

---

## English

### Scope

Every program file inside the Windows release zip is signed:

`UpDevice.Launcher.exe`, `UpDevice.Service.exe`, `UpDevice.Tray.exe`, and the ATS/ETS2 telemetry plugin `UpDevice.Telemetry.dll`.
Third-party runtime libraries and .NET runtime files are signed by their respective publishers.

### Free code signing

This is an MIT-licensed open source project. Windows releases are planned to be signed through the
**SignPath Foundation** free code signing program for open source projects:

- The certificate is held by the SignPath Foundation; the private key lives in their HSM and is never exported.
- Each release is verified by SignPath to be built by this repository's GitHub Actions workflow before signing.
- Service: <https://signpath.org/>

> Status: application and review are in progress. **Releases published so far carry no trusted signature**,
> so Windows SmartScreen may warn on first run. Workaround below.

### Team roles

Single maintainer project:

| Role | Person | Responsibility |
| --- | --- | --- |
| Author | [@uu123bb](https://github.com/uu123bb) | writes code, triggers releases |
| Reviewer | same | reviews commits and release content |
| Approver | same | approves each signing request on SignPath |

All participants use multi-factor authentication for GitHub and SignPath.

### Privacy

This program collects no personal data for third parties. It reads and writes only:

- the remote endpoint, `SECRET`, device id and display name you configure (stored locally);
- foreground process/window title, idle time, power status, currently playing media, controller input;
- when "Roads & Vehicles" is enabled, in-game telemetry values (speed, fuel, job data).

Everything is sent only to the Sleepy server you configure — never to the author or any third party.

### System changes

| Change | Location | When |
| --- | --- | --- |
| config file | `data/config.json` | first run |
| logs | `data/Logs/` | while running |
| autostart (tray) | `HKCU\...\CurrentVersion\Run` | when you enable it |
| tray at logon | scheduled task `UpDevice.Tray` (`schtasks`, `ONLOGON` + `HIGHEST`) | when you enable it |
| telemetry plugin | `<game>\bin\win_x64\plugins\UpDevice.Telemetry.dll` | when you click Install |

No Windows service, no drivers, nothing written outside the program directory and the locations above.

### Uninstall

Exit from the tray menu, delete the program directory (including `data/`).
Toggle off autostart to remove the Run entry and scheduled task; click Uninstall in the telemetry section to remove the plugin DLL.

### Verifying a signature

```powershell
signtool verify /pa /v .\UpDevice.Launcher.exe
```

### If Windows blocks the first run

```powershell
Unblock-File .\UpDevice.Launcher.exe
```

Or right-click the file → Properties → check "Unblock".
