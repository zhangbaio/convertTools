# TikTokPublisher（TikTok 多账号上传）

从 `ChannelsPublisher` **复制并独立演进**的 TikTok 短剧中心发布模块，**不依赖 Python `tiktok_uploader_client`**。

## 目标

- 多账号 + 每账号独立 WebView2 会话（切换账号只改 `IsVisible`，不重启浏览器）
- 每个账号可独立选择任意工作目录，队列项目自身记录执行账号
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
| `.tiktok-uploader-workspace.json` | 旧版遗留文件，当前版本不再读写 |
| `.tiktok-task-queue.db` | 队列状态 SQLite |
| `{project}/workflow/tiktok-upload-state.json` | 编辑草稿 URL 缓存 |

## 已实现功能

- 多工作目录并行队列 + 多账号内嵌浏览器后台上传
- 每账号独立 CDP 端口与静态 IP 代理
- 队列 Worker（预处理 + upload_series）
- 短剧下载 / AI 改写 / 海报（ShortDrama C#）
- 红果 APP 下载的协议、两条取流链路和解密记录见 `src/ShortDrama/docs/hongguo/README.md`

## 剧情高光

在「素材剪辑 → 全局剪辑配置 → 高光」勾选「剧情高光」并保存。剪辑时使用字幕或 ASR 的对白时间，以及 FFmpeg 检测的镜头切点，优先在对白间隙和换镜点剪切。配置 AI 文本接口后，模型可从已有候选中挑选并排列跨集剧情。AI 不可用或方案未通过校验时，使用本地选段和对白切点保护。

剧情高光仍按每条成片的时长范围和相似度配置出片。开启或关闭该选项会重新生成高光成片，同一配置下重新运行可复用已完成的视频。算法参考 [DramaClip v0.2.5](https://github.com/efarsoft/DramaClip/releases/tag/v0.2.5)。

当对白和镜头切点能定位重生等时空转场时，剧情高光会保留转场画面，并按剧情顺序衔接前后片段。结尾有问句时，会优先接上附近可确认的决定或答复，再复核时长和相似度。

高光页可另行开启「本地 OCR 补充硬字幕」和「画面复评」。OCR 通过本机 Windows OCR 语言包读取硬字幕，补充缺失对白和选段线索。画面复评向所选视觉模型发送少量候选截帧，补选有画面依据的无对白或少对白镜头。可在「服务配置」指定支持图片输入的 OpenAI 兼容接口，例如本地 Qwen3-VL。留空时复用 AI 文本服务。识别或复评失败时保留现有素材分析结果。
