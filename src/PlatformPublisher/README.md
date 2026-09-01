# 云帆多平台发布助手

这是与 `TikTokPublisher` 完全分离的桌面应用。它拥有独立的程序集、启动入口和任务存储，不读写 TikTok 助手的账号与队列数据库。

## 模块边界

```text
PlatformPublisher.Common
├─ 平台、账号、任务模型
├─ 独立账号/任务持久化
├─ 定时与中断恢复策略
└─ 平台适配器接口与协调器

PlatformPublisher.Weixin
├─ 视频号登录与剧集上传适配器
├─ 目录批量发表
└─ 系统高光发表

PlatformPublisher.Kuaishou
├─ 快手分账个人适配器
└─ 快手分账企业适配器

PlatformPublisher.Desktop
├─ 顶部平台导航与依赖组合
├─ 视频号页面直接承载 `ChannelsPublisher.Ui.MaterialPublishView`
├─ 快手个人/企业独立任务页面
└─ 公共系统设置页面
```

短剧下载、工程图、成本报表和数据链路继续由现有 `ShortDrama.Core` / `ShortDrama.Infrastructure` 提供。平台模块只能依赖公共模块；公共模块不反向引用视频号、快手或 TikTok。

视频号账号栏、WebView2 多账号会话、素材队列、并发发布和断点续传不再在 `PlatformPublisher.Desktop` 重复实现，统一复用 `ChannelsPublisher.Ui`。`PlatformPublisher.Weixin` 保留剧集上架、系统高光、高级配置覆盖和 AI/ASR 注入等扩展服务，后续通过适配层接入这套现有视频号页面。

## 当前能力

- 视频号：复用 `ShortDrama.Infrastructure` 的正式登录和剧集上传链路。
- 视频号目录批量发表：每个一级子目录作为一条素材，优先读取 `description.txt`、`desc.txt` 或 `描述.txt`，否则使用目录名作为描述；自动选择目录内体积最大的视频。
- 批量与定时：支持顺序执行当前平台待办任务；应用保持运行时每 30 秒检查到期任务，异常退出后会把遗留的“执行中”任务恢复为待执行。
- 视频号系统高光：支持按剧名、数量和“混剪/解说/切片”类型发表，可选择发表后重新生成高光。
- 视频号普通素材：支持项目 `material-videos/videos`、本地目录顶层视频和手工选择多个视频文件，复用视频号描述、原创、位置和重复发表选项。
- 视频号剧集任务覆盖：读取项目原始 JSON，在独立任务目录生成临时配置，可按高级配置筛选上传集数；原始项目配置保持只读。
- AI/ASR 配置注入：视频号素材配置从多平台独立系统设置读取 AI Endpoint、Key、模型、超时和 ASR 参数，不读取 TikTok 助手设置库。
- 视频号二级导航：视频号助手内提供“多账号素材发布”“剧集上架”和“项目流水线”三个 TAB；三者复用左侧同一账号。项目流水线支持扫描工作根目录，并已接入剧集、目录素材、系统高光、项目素材、本地视频和自选视频任务，支持定时执行、失败重试、人工介入、持久化队列及独立运行日志；同时复用公共短剧服务执行下载、改写、海报、转码、字段补齐、成本报表、工程图和素材校验。
- 多账号档案：各平台账号分别保存；任务绑定稳定账号 ID，修改昵称不会切换授权目录，删除档案不会删除授权文件和历史任务。
- 快手分账个人版、企业版：已建立独立任务类型与队列隔离；由于参考仓库没有对应自动化源码，当前明确标记为“待接入”，不会错误调用其他平台。

## 启动

```powershell
& "D:\code\convertTools-main\.dotnet\dotnet.exe" run --project "D:\code\convertTools-main\src\PlatformPublisher\PlatformPublisher.Desktop\PlatformPublisher.Desktop.csproj" -c Release
```

独立任务存储位置：`%LocalAppData%\YunfanPlatformPublisher\publish-jobs.json`。
