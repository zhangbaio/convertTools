# ChannelsPublisher（视频号多账号发布）— P0 骨架

发布素材迁到 .NET 的落地项目。P0 = 可离线构建运行的 Avalonia 多账号发布骨架
（账号列表 + 会话持久化模型 + 浏览器抽象/占位）。WebView2 内嵌浏览器作为唯一待联网补的一块。

## 结构（清洁架构，与 video-downloader 一致）

```
src/ChannelsPublisher.Core         领域层（无 UI 依赖）
  Models/PublishAccount.cs         账号：Id/Name/Nickname/ProfileDir/Status
  Models/AccountStatus.cs          在线/登录中/离线
  Services/AppPaths.cs             数据目录：%LocalAppData%/ChannelsPublisher
  Services/AccountStore.cs         账号 JSON 读写 + 每账号独立会话目录管理
  Abstractions/IEmbeddedBrowser.cs 内嵌浏览器抽象（WebView2 的接口槽位）

src/ChannelsPublisher.Desktop      Avalonia UI（MVVM: CommunityToolkit.Mvvm）
  Views/MainWindow.axaml           左=账号列表+增删/登录；右=浏览器宿主占位；底=状态栏
  ViewModels/MainViewModel.cs      账号集合 + AddAccount/RemoveAccount/Login 命令
  ViewModels/AccountItemViewModel  列表项（可观察 Name/Status）
```

## 运行

```powershell
$dotnet = "..\.dotnet\dotnet.exe"
& $dotnet run --project src\ChannelsPublisher.Desktop
```
（Avalonia/MVVM 包已在本机 NuGet 缓存，可离线构建。已验证：build 0 错误、启动无异常。）

## P0 已完成

- 多账号发布主界面骨架（对应参考图：左账号列表 / 右浏览器区 / 底状态）。
- 账号增删 + JSON 持久化；**每账号预建独立会话目录** `profiles/<id>/`（= WebView2 UserDataFolder，登录态隔离长存）。
- `IEmbeddedBrowser` 抽象隔离内嵌浏览器，上层面向接口编程。

## WebView2 已集成并验证 ✅

- `Controls/WebView2Host.cs`：`NativeControlHost` + `CoreWebView2`，实现 `IEmbeddedBrowser`。
  - 每账号 `UserDataFolder = account.ProfileDir`（登录态隔离，长存）。
  - `AdditionalBrowserArguments = "--remote-debugging-port=<9222+idx>"` 暴露 CDP。
  - `MainWindow` 每账号一个 WebView2Host 挂在 `BrowserArea`，靠 `IsVisible` 切换显示（隐藏账号仍存活）。
- **已验证**（spike `cdpshot 9222` 连内嵌 WebView2）：WebView2 ready、渲染出视频号登录页
  （Title=视频号助手）、Playwright `ConnectOverCDP` 连上并截图成功 → 自动发布可经此驱动。
  运行日志：`%Temp%/webview2-host.log`。

## 关键坑（已解决，勿踩）

1. **不要手写 `InitializeComponent()`**：会覆盖 Avalonia NameGenerator 生成的（含命名字段赋值）那个，
   导致 `BrowserArea`/`EmptyHint` 为 null。构造函数直接调 `InitializeComponent()`（生成的）即可。
2. **WebView2 必须在窗口 `OnOpened` 之后创建**（此时才有原生窗口句柄），不能在 `DataContextChanged` 早期建。
3. WebView2 元包带 WPF 变体 → `WindowsBase` 冲突警告，无害（不用 WPF 控件）。

## P2 已完成：自动发布 + 并发 ✅

- `Core/Publishing/`：领域模型 + `IPublishAutomation` + `PublishScheduler`
  （`SemaphoreSlim(maxParallelAccounts)` 门控账号间并行 + 账号内 `foreach` 串行）。
  并发语义单测通过：`tests/ChannelsPublisher.SchedulerCheck`（`dotnet run` → 全局并发峰值==上限、账号内==1）。
- `Desktop/Services/PlaywrightPublishAutomation`：`Microsoft.Playwright` `ConnectOverCDPAsync(账号CdpEndpoint)`
  驱动该账号 WebView2，跑 P1 全流程（上传→描述→短标题→封面→挂载视频号剧集→原创声明→FinalAction）。
  `FinalAction` 默认 `None`（只填不发，安全）/`Draft`/`Publish`。
- UI：工具栏「发布测试」→ 选视频 → 发到当前账号（`FinalAction.None`），进度进状态栏。

试用：先给某账号扫码登录（点账号→登录→在内嵌 WebView2 里扫码），再点「发布测试」选个视频。

## P3 已完成：UI 批量发布 + 接素材 ✅

底部「发布任务」面板：
- **素材→账号分配**：「添加素材」把选中视频分配给当前账号；每条任务=一素材+一目标账号。
- **导入任务(JSON)**：消费 Python prep 产出的发布任务清单（契约见 `docs/publish-tasks.sample.json`），
  按 `account`（账号名/Id）匹配账号；同时套用文件里的 `finalAction`。
- **结束动作** 下拉：只填不发（默认，安全）/ 保存草稿 / 直接发表。
- **并发账号** 上限（NumericUpDown）。
- **开始发布**：按账号分组 → 取各账号 WebView2 `CdpEndpoint` → `PublishScheduler` 并发跑
  （账号间并行、账号内串行）→ 进度回填每条任务状态（待发布/发布中/完成/失败）。
- **清空已完成**。

试用：给账号扫码登录 → 「添加素材」或「导入任务」 → 选结束动作 → 「开始发布」。

## 契约（Python prep → .NET）

`docs/publish-tasks.sample.json`：
```json
{ "finalAction": "none|draft|publish",
  "tasks": [ { "account": "账号名", "videoPath": "...", "description": "...",
    "shortTitle": "...", "coverPath": "...", "dramaName": "...", "declareOriginal": true } ] }
```

## P4 已完成：素材准备（prep）C# 化 ✅

`src/ChannelsPublisher.Prep`（库）+ `ChannelsPublisher.Prep.Cli`（控制台）。四阶段忠实移植现有 Python：

| 阶段 | 实现 | 说明 |
|---|---|---|
| 来源扫描 | `SourceScanner` | 按 `sourceType` 分支，覆盖 Python 全部来源：`directory` / `new_drama_mount`（标题取 `shortdrama-project.json`）/ `downloaded_system_highlight`·`material_video_download`（`.publish.json`+`.cover.*`+manifest）/ `material_clips`（素材剪辑输出）/ `project_materials`（material-videos）/ `source_videos`（直下）/ `custom_files`（显式列表 `customFiles`）/ `directory_publish`（子目录取最大视频+`description.txt`，标签归一化）；自然排序（第2集<第10集） |
| 原创度 | `OriginalityPlanBuilder` + `FfmpegOriginalityProcessor` | 移植 ffmpeg 滤镜（zoom/eq/setpts+atempo/fade）；种子=文件名→确定性；数字 InvariantCulture |
| 封面 | `CoverResolver` | 旁车图（`<stem>.cover.jpg`/`<stem>.jpg`）或 ffmpeg 抽帧 |
| AI 描述 | `AiDescriptionService` | OpenAI 兼容 chat/completions（豆包 ark 等），失败回退基础文案 |
| 编排 | `PrepPipeline` | 扫描→逐条(原创度→封面→AI)→产出 `publish-tasks.json` |

运行：
```powershell
& $dotnet run --project src\ChannelsPublisher.Prep.Cli -- docs\prep-config.sample.json
# 产出 <OutputDir>\publish-tasks.json → 在发布应用里「导入任务」
```
配置示例 `docs/prep-config.sample.json`。纯逻辑单测：`tests/ChannelsPublisher.PrepCheck`（原创度确定性 + 数字格式 + 扫描）。
ffmpeg/AI 端到端需真 ffmpeg(libx264) + 视频 + AI key。

## 完整链路

```
素材源 ──(C# prep: 扫描/原创度/封面/AI描述)──▶ publish-tasks.json
        └─(或复用现有 Python prep 产同格式)─┘
                         │  发布应用「导入任务」
                         ▼
   多账号内嵌 WebView2 ──(CDP 驱动)──▶ 视频号并发发布（账号间并行/账号内串行）
```
P1（可行性）→ P0（骨架+WebView2）→ P2（自动化+并发）→ P3（UI+批量）→ P4（prep C# 化）全部完成。
