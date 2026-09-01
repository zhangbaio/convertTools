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
└─ 只负责页面、命令和依赖组合
```

短剧下载、工程图、成本报表和数据链路继续由现有 `ShortDrama.Core` / `ShortDrama.Infrastructure` 提供。平台模块只能依赖公共模块；公共模块不反向引用视频号、快手或 TikTok。

## 当前能力

- 视频号：复用 `ShortDrama.Infrastructure` 的正式登录和剧集上传链路。
- 视频号目录批量发表：每个一级子目录作为一条素材，优先读取 `description.txt`、`desc.txt` 或 `描述.txt`，否则使用目录名作为描述；自动选择目录内体积最大的视频。
- 批量与定时：支持顺序执行当前平台待办任务；应用保持运行时每 30 秒检查到期任务，异常退出后会把遗留的“执行中”任务恢复为待执行。
- 视频号系统高光：支持按剧名、数量和“混剪/解说/切片”类型发表，可选择发表后重新生成高光。
- 多账号档案：各平台账号分别保存；任务绑定稳定账号 ID，修改昵称不会切换授权目录，删除档案不会删除授权文件和历史任务。
- 快手分账个人版、企业版：已建立独立任务类型与队列隔离；由于参考仓库没有对应自动化源码，当前明确标记为“待接入”，不会错误调用其他平台。

## 启动

```powershell
& "D:\code\convertTools-main\.dotnet\dotnet.exe" run --project "D:\code\convertTools-main\src\PlatformPublisher\PlatformPublisher.Desktop\PlatformPublisher.Desktop.csproj" -c Release
```

独立任务存储位置：`%LocalAppData%\YunfanPlatformPublisher\publish-jobs.json`。
