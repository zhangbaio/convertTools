# 云帆多平台发布助手

这是与 `TikTokPublisher` 完全分离的桌面应用。它拥有独立的程序集、启动入口和任务存储，不读写 TikTok 助手的账号与队列数据库。

平台助手使用独立的两级 SQLite：`%LocalAppData%\YunfanPlatformPublisher\app.db` 保存全局配置、平台账号、发布任务、逐素材事件和统计数据；项目目录中的 `.yunfan-platform.db` 保存快手上传断点、ADX批次等可随项目移动的状态。旧 JSON/旧数据库首次启动时幂等导入并保留为兼容备份。

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
- 视频号 ADX 素材：支持独立加密账号与登录态、按原剧名查询、候选指标选择、1–5 路并发下载、ADX/项目封面、v1/v2 下载批次恢复，以及下载完成后创建持久化任务并自动保存到视频号草稿。每条素材完成后立即按账号回写批次清单，重试不会覆盖历史成功项。
- 数据统计：顶部独立“数据统计”页面汇总本地逐素材发布活动、视频号首页/剧集收入/日指标及快手个人版按短剧 T-1 指标；支持日期、平台、账号关键词筛选，视频号最多 31 天补采、按账号互斥、停止续跑、成功缓存保护、CSV 导出和旧云帆 SQLite/快照导入。视频号与快手收入分别展示，不跨平台混算。
- 视频号剧集任务覆盖：读取项目原始 JSON，在独立任务目录生成临时配置，可按高级配置筛选上传集数；原始项目配置保持只读。
- AI/ASR 配置注入：视频号素材配置从多平台独立系统设置读取 AI Endpoint、Key、模型、超时和 ASR 参数，不读取 TikTok 助手设置库。
- 视频号二级导航：视频号助手内提供“多账号素材发布”“账号配置”“剧集上架”“项目流水线”“短剧下载”和“已归档项目”六个 TAB，共用左侧账号区。短剧下载页支持搜索/今日短剧、批量选择、指定集数下载并自动加入视频号队列。项目流水线支持扫描工作根目录、导入多个本地项目、按剧名创建项目，并已接入剧集、目录素材、系统高光、项目素材、本地视频和自选视频任务，支持筛选分页、逐步骤状态持久化与中断恢复、ASR/剧情信号驱动的智能重剪、AI 制作证明、可信时间戳本地模板证书（非第三方 TSA 认证）、自动扫描未上架剧集并连续上架、短剧资料/上传状态/上传者同步管理系统、勾选式批量执行/重置/改状态/改归属账号/移出、保存步骤设置、定时执行、失败重试、人工介入、待上传优先、上传后自动归档、持久化队列及独立运行日志；同时复用公共短剧服务执行下载、改写、海报、转码、一键修复、无损重封装、字段补齐、成本报表、工程图和素材校验。归档页可按工作根目录读取归档元数据并打开归档、源项目和工作项目目录。
- 视频号账号配置：二级导航新增“账号配置”TAB，可编辑左侧当前账号的名称和视频号昵称，查看稳定账号 ID、独立浏览器会话目录及最近登录时间；浏览器会话就绪后自动持久化登录时间。
- 视频号预上传校验：提供 `tools/PreUploadSmoke` C# 冒烟工具，可在隔离副本中执行下载及全部上传前步骤、对比模板产物并输出 JSON 报告；转码前拒绝伪 1080P 低码率源，短剧信息会按最终工作视频纠正集数/时长并保留已有标签、短标题和成本字段。AI 制作证明复用公共媒体探测/抽帧能力，输出 4 张带真实关键帧、媒体参数和 SHA-256 指纹的视频号专用 PNG 记录页。
- 多账号档案：各平台账号分别保存；任务绑定稳定账号 ID，修改昵称不会切换授权目录，删除档案不会删除授权文件和历史任务。
- 快手分账个人版：已接入独立经营者管理平台入口、账号级登录态、扫码登录、项目产物解析、第一页表单/封面/承诺函/工程图、单集数量与竖屏封面批量设置、分集视频批量上传、进度和失败检测，以及保持待审核/提交审核终态；提交审核后必须检测到平台“提交成功/审核中”等最终状态才会完成任务。默认先保存草稿，配置明确设为 `next` 后才继续视频上传。上传状态保存在 workflow 的 `.kuaishou-personal-upload-state.json`，记录 `miniSeriesId`、当前阶段和失败信息，重试时可直接进入编辑页并跳过已上传视频；编辑模式缺少短剧 ID 时会按剧名从内容管理列表查找，找不到则停止以避免重复创建。`runMode` 支持 `auto/create/edit`，`forceRerun=true` 可忽略旧状态。企业版仍保持独立“待接入”状态，不会错误调用个人版或其他平台。
- 快手分账配置：个人版任务页提供账号级“登录授权、承诺函、基础字段、分账配置”四个可视化 TAB，覆盖人员、快手 ID、浏览器模式、固定承诺函 PDF、制作/播出字段、演员、售卖字段和提交/续传策略；配置保存到 `%LocalAppData%\YunfanPlatformPublisher\kuaishou-personal\accounts\<账号ID>`，旧任务未指定配置文件时也会读取对应账号默认配置，不写入 TikTok 助手设置库。

## 启动

```powershell
& "D:\code\convertTools-main\.dotnet\dotnet.exe" run --project "D:\code\convertTools-main\src\PlatformPublisher\PlatformPublisher.Desktop\PlatformPublisher.Desktop.csproj" -c Release
```

源码启动、任务栏、发布 EXE、安装程序和安装后的快捷方式统一使用
`PlatformPublisher.Desktop/Assets/jushu-assistant-logo-v2.ico`。

## 打包

```powershell
& "D:\code\convertTools-main\packaging\package-platform-installer.ps1" -Version "1.0.0"
```

仅生成发布目录、不编译安装程序：

```powershell
& "D:\code\convertTools-main\packaging\package-platform-installer.ps1" -Version "1.0.0" -SkipInstallerCompile
```

发布目录为 `artifacts/publish/YunfanPlatformPublisher`，安装包输出到 `artifacts/INSTALL`。

独立任务存储位置：`%LocalAppData%\YunfanPlatformPublisher\publish-jobs.json`。
