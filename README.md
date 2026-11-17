# UpDevice

## 这是什么？ / What is this?
[Switch to English (Not complete)](/README_EN.md)

**UpDevice** 是一个能获取设备当前前台信息，并将这些信息转发到用户所指定的 **Sleepy** 实例

### Sleepy 是什么？ / What is Sleepy?
[Sleepy](https://github.com/sleepy-project/sleepy) 是一个提供网页服务的 Flask 应用，旨在显示使用上报工具上报的信息（包括但不限于本项目）

欲了解 **Sleepy** 的细节，请移步 [Sleepy 源仓库](https://github.com/sleepy-project/sleepy)

欲了解 **UpDevice** 的细节，请移步[技术文档](/README_AI.md)或[代码签名细则](/CODE_SIGNING.md)

## 特性 / Feature
- 相较于 [Sleepy 源仓库](https://github.com/sleepy-project/sleepy) 提供的 Python 上报脚本，该项目使用了 C# .NET 开发，只需要安装 .NET Runtime 10 即可使用
- 支持快捷设置计划任务实现开机启动
- 支持欧卡2/美卡的遥测数据上报
- 支持自动更新（通过 GitHub Release）

## 快速开始 / Quick Start

## 应用签名 / Application Signing
项目后期将使用 SignPath Foundation 提供的签名证书为主程序签名，具体可查看[代码签名细则](/CODE_SIGNING.md)
