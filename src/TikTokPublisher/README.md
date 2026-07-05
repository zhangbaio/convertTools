# TikTokPublisher（TikTok 多账号上传）

从 `ChannelsPublisher` **复制并独立演进**的 TikTok 短剧中心发布模块，**不依赖 Python `tiktok_uploader_client`**。

## 目标

- 多账号 + 每账号独立 WebView2 会话（切换账号只改 `IsVisible`，不重启浏览器）
- 工作目录 ↔ 账号绑定（`.tiktok-uploader-workspace.json`）
- Playwright CDP / 内嵌 WebView2 驱动 TikTok 上传
- 红果下载、AI 改写、海报等由本仓库 `ShortDrama.Infrastructure`（C#）实现

## 结构

```
src/TikTokPublisher/
  TikTokPublisher.Core/     领域层：账号、调度器、队列、工作目录
  TikTokPublisher.Ui/       Avalonia 视图 + WebView2 + Playwright 自动化
  TikTokPublisher.Desktop/  独立 WinExe 入口
```

## 运行

```powershell
cd D:\code\convertTools-main
dotnet run --project src\TikTokPublisher\TikTokPublisher.Desktop
```

## 数据目录（独立）

`%USERPROFILE%\.tiktok_publisher`

| 文件/目录 | 说明 |
|---|---|
| `accounts.json` | 账号列表 |
| `active-account.json` | 当前选中账号 |
| `app.db` | 全局设置、执行历史、短剧下载队列状态 |
| `profiles/<id>/` | 每账号 WebView2 UserDataFolder |
| `profiles/<id>/tiktok_auth_state.json` | Playwright storage_state |
| `license_state.bin` | 授权登录状态（系统服务，C# 客户端独立） |

工作目录内：

| 文件 | 说明 |
|---|---|
| `.tiktok-uploader-workspace.json` | 工作目录 ↔ 账号绑定 |
| `.tiktok-task-queue.db` | 队列状态 SQLite |
| `{project}/workflow/tiktok-upload-state.json` | 编辑草稿 URL 缓存 |

## 已实现功能

- 多工作目录并行队列 + 多账号内嵌浏览器后台上传
- 每账号独立 CDP 端口与静态 IP 代理
- 队列 Worker（预处理 + upload_series）
- 短剧下载 / AI 改写 / 海报（ShortDrama C#）
- 静音检测与修复（本地 ASR / ffmpeg）
