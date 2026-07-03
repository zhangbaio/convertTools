# TikTokPublisher（TikTok 多账号上传）

从 `ChannelsPublisher` **复制并独立演进**的 TikTok 短剧中心发布模块，**不修改原 ChannelsPublisher 代码**。

## 目标

- 多账号 + 每账号独立 WebView2 会话（切换账号只改 `IsVisible`，不重启浏览器）
- 工作目录 ↔ 账号绑定（兼容 Python 客户端 `.tiktok-uploader-workspace.json`）
- Playwright CDP 驱动 TikTok 上传（`browser_actions.py` 逐步移植）
- **暂不实现**飞书长连接命令机器人
- 红果下载等业务逻辑以 Python 项目 `tiktok_uploader_client` 为准，本模块专注发布壳

## 结构

```
src/TikTokPublisher/
  TikTokPublisher.Core/     领域层：账号、调度器、工作目录绑定
  TikTokPublisher.Ui/       Avalonia 视图 + WebView2 + Playwright 自动化
  TikTokPublisher.Desktop/  独立 WinExe 入口
```

## 运行

```powershell
cd D:\code\convertTools-main
dotnet run --project src\TikTokPublisher\TikTokPublisher.Desktop
```

## UI 布局

对齐 Python `tiktok_uploader_client` 工作台：

- 顶部：**TikTok 短剧助手** 品牌 + 模块 Tab（短剧下载 / TikTok 上传 / 账号管理 / 运行日志 / 浏览器）
- 左侧：**全局账号侧栏**（250px，搜索、添加/配置/登录/同步）
- 主区：**TikTok 上传** 页 — 工作目录控制区、启用步骤、17 列队列表格
- 底部：状态栏 + 「今日上传完成」计数
- 主题：`Themes/PythonAppTheme.axaml`（米色面板 `#fff9f2`、主色 `#0f7ae5`）

WebView2 浏览器在 **「浏览器」** Tab；队列执行时自动切到浏览器会话。

### 已迁移页面（对齐 Python）

| 页面 | C# 视图 | 说明 |
|---|---|---|
| 短剧下载 | `DramaDownloadView` | 搜索 / 下载队列 / 设置；队列状态写入 Python DB `drama_download_queue_state` |
| 账号管理 | `AccountProfilesView` | 四 Tab 编辑器（登录/网络/发布/基础），保存后同步 Python DB |
| 运行日志 | `LogView` | 项目列表 + 日志区；队列/下载日志汇总，支持筛选/复制/停止 |

## 数据目录

与 Python 客户端共用：`%USERPROFILE%\.tiktok_uploader_client`

- `tiktok-accounts.json` — 账号列表（C# 新格式）
- `active-tiktok-account.json` — 当前 active 账号指针（切换时只写此文件，避免全量 profile 同步卡顿）
- `tiktok_uploader.db` — Python SQLite（`tiktok_account_profiles` / `account_auth_states` / `app_state`）
- `profiles/<id>/` — 每账号 WebView2 UserDataFolder
- `profiles/<id>/tiktok_auth_state.json` — Playwright storage_state（与 Python 兼容）

工作目录（与 Python 共用）：

- `.tiktok-uploader-workspace.json` — 工作目录 ↔ 账号绑定
- `.tiktok-task-queue.db` — 队列状态 SQLite
- `{project}/workflow/tiktok-upload-state.json` — 编辑草稿 URL 缓存
- `{project}/workflow/tiktok_upload_videos/` — 上传前 staging 目录

## 已实现功能

| 阶段 | 内容 |
|---|---|
| 队列 Worker | `QueueWorkerRunner` 按 Python `STEP_ORDER` 跑 small_video_repair → silence_detect → silence_repair → material_validate → upload_series；队列表格 UI + 启动/停止/绑定账号 |
| 代理 + upload_state | WebView2 `--proxy-server` + Basic Auth；`TikTokUploadStateStore` 写 `tiktok-upload-state.json`（SQLite `tiktok_upload_state` 待补） |
| 素材校验 / 预处理 | ffmpeg/ffprobe 探测、小视频 MP4 padding、静音检测与首尾 trim；`TikTokUploadStagingService` staging |
| 账号双向同步 | 启动时从 `tiktok_uploader.db` 合并导入；保存/切换/删除时自动写回；工具栏「同步 Python 账号」手动双向同步 |

账号同步行为：

- **导入**：同 `profile_id` 合并字段，保留本地 `ProfileDir`；支持 legacy `client_settings.tiktok_account_profiles_json`
- **导出**：upsert `tiktok_account_profiles` + `account_auth_states`，更新 `app_state.active_tiktok_account_profile_id`；read-modify-write `payload_json` 保留 Python 独有字段
- **开关**：`AccountStore.AutoImportFromPythonOnLoad` / `AutoSyncToPythonDatabase`（默认均为 true）

## 与 ChannelsPublisher 的差异

| 项 | ChannelsPublisher | TikTokPublisher |
|---|---|---|
| 目标站点 | 微信视频号 | TikTok 短剧中心 |
| 账号切换 | 改 visible | 同左 + active 指针独立持久化 |
| 工作目录 | 无 | `.tiktok-uploader-workspace.json` 绑定 |
| 飞书 | 无 | 暂不实现长连接机器人 |
| Prep/Clip | 有 | 无（红果等沿用 Python） |

## 待办（下一阶段）

1. ~~移植 `browser_actions.py` 核心填表/上传/提交流程~~（已完成首版，见 `Ui/Services/TikTok/`）
2. ~~工作目录扫描（基础版）~~（`WorkspaceProjectScanner`，UI 左下角项目列表）
3. ~~队列状态持久化（`.tiktok-task-queue.db` / SQLite）~~（`WorkspaceQueueDatabase` + `WorkspaceQueueService`）
4. ~~账号管理页（代理、合同 ID、付费比例等完整字段编辑）~~（`AccountSettingsDialog` 三 Tab）
5. ~~从 Python SQLite 迁移导入 / 双向同步~~（`PythonProfileImporter` + `PythonAccountDatabaseSync` + 「同步 Python 账号」）
6. ~~编辑流（`fill_tiktok_edit_publish_form`）~~（`TikTokEditFlowService` + `TikTokBrowserActions.Edit`）
7. ~~分批上传（`batch_upload_service`）~~（`TikTokBatchUploadService`）
8. ~~队列 Worker + 素材预处理 pipeline~~（Phase 1–3）
9. upload_state 写回 Python DB `tiktok_upload_state` 表
10. 静音中段 ASR 修复（需 Python ASR 服务）
11. download / rewrite / poster 等 Python 专属步骤（本模块不实现）
12. 队列并发数对齐 Python 配置
