# 云帆多平台发布助手

这是与 `TikTokPublisher` 完全分离的桌面应用。它拥有独立的程序集、启动入口和任务存储，不读写 TikTok 助手的账号与队列数据库。

## 当前能力

- 视频号：复用 `ShortDrama.Infrastructure` 的正式登录和剧集上传链路。
- 视频号目录批量发表：每个一级子目录作为一条素材，优先读取 `description.txt`、`desc.txt` 或 `描述.txt`，否则使用目录名作为描述；自动选择目录内体积最大的视频。
- 快手分账个人版、企业版：已建立独立任务类型与队列隔离；由于参考仓库没有对应自动化源码，当前明确标记为“待接入”，不会错误调用其他平台。

## 启动

```powershell
& "D:\code\convertTools-main\.dotnet\dotnet.exe" run --project "D:\code\convertTools-main\src\PlatformPublisher\PlatformPublisher.Desktop\PlatformPublisher.Desktop.csproj" -c Release
```

独立任务存储位置：`%LocalAppData%\YunfanPlatformPublisher\publish-jobs.json`。
