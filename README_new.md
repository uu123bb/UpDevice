# UpDevice

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

## 计划

- [x] 基本完成上报部分
- [x] 实现控制器防休眠
- [x] 自动更新（使用启动器）
- [ ] 适配 [Fork](https://github.com/uu123bb/sleepy_serverless) 的心跳存活模式（待开发）
