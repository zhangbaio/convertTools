# P1 可行性验证：视频号上传经 .NET / CDP 驱动

目标：迁移「发布素材」到 .NET(Avalonia) 前，先验证**最大风险点**——能否用 .NET 经 CDP
驱动（含内嵌 CEF 模型）完成视频号视频上传。

## 结论（已在本机离线跑通）

`probe` 模式（无头 / 离线 / 无需登录）对本地 weui 同构页面验证三条路径，**全部 PASS**：

| 路径 | 含义 | 结果 |
|---|---|---|
| A. Playwright `SetInputFilesAsync` + **真实生产选择器** | 高层 API 上传 | ✅ |
| B. 原生 CDP `DOM.setFileInputFiles` | CEF 必须支持的底层原语 | ✅ |
| C. `ConnectOverCDP` 连外部浏览器 + setInputFiles | **CEF 内嵌驱动模型** | ✅ |

即：**.NET 能经 CDP 用 `setInputFiles` 上传文件，且能连接「外部/内嵌浏览器的 CDP 端点」驱动**
（视频号上传的关键动作 = 对 `input[type=file]` 设文件，正是 A/B/C 证明的能力）。

真实选择器（取自现有 Python 生产代码 `autogen_config_builder.py` / `upload_queue.py`）：
```
.weui-desktop-form__control-group_label-r:has(.weui-desktop-form__label:has-text("选取视频")) input[type=file]
```
上传完成判定：轮询页面文本 `已上传成功 n/m`；错误文案 `上传失败/格式不支持/超出限制/...`。

## 运行

需要本机 .NET 8（仓库自带 `..\..\.dotnet\dotnet.exe`）。Playwright.NET 1.52.0 与 Chromium 已在
本机缓存，无需联网。

```powershell
$dotnet = "..\..\.dotnet\dotnet.exe"
# 1) 本机可行性 probe（无头，立即可跑）
& $dotnet run --project . -c Release -- probe

# 2) 真实视频号上传（有头，需扫码登录 + 一个真实视频文件）
& $dotnet run --project . -c Release -- channels "D:\path\to\test.mp4"
```

`channels` 模式：首次打开视频号登录页→扫码登录（登录态存 `%TEMP%\channels-spike-profile`，
对应最终架构「每账号一个独立 profile」）→进发表页→定位视频输入→`SetInputFilesAsync`→
轮询直到「已上传成功」。**安全起见不点「确认提审/发表」**。

> `SPIKE_CHROME` 环境变量可覆盖 Chromium 路径（默认指向本机 ms-playwright 的 chromium-1169）。

## 真机端到端结果（已跑通 ✅）

用 Edge + 昭服登录态 + 真实项目视频，`real`/`dump`/`fill` 三模式验证：

- **登录态复用**：`StorageStatePath` 指向 `wx_auth_state.json`（Playwright storageState），免扫码进入发表页 ✅
- **视频上传**：`input[type=file][accept*=video]` + `SetInputFilesAsync` → 视频被接收、生成封面 ✅
- **两个坑（迁移必读）**：
  1. **编解码器**：Playwright 自带 Chromium 缺 H.264/AAC → 视频号报「当前浏览器不支持此视频格式」。**用 `Channel="msedge"`（Edge 含专有编解码器）解决**。迁移内嵌浏览器要选带编解码器的（WebView2 天生有；CEF 需 proprietary-codecs 构建）。
  2. **wujie 微前端 + Shadow DOM**：发表表单在 open Shadow DOM 里，`document.querySelector`/`page.content()` 拿不到；**Playwright 选择器自动穿透 open shadow DOM**（原生 CDP `DOM.querySelector` 默认不穿透，迁移需注意）。
- **逐项表单选择器（已验证）**：

| 表单项 | 选择器 | 操作 |
|---|---|---|
| 视频文件 | `input[type=file][accept*='video']` | SetInputFiles |
| 视频描述 | `div.input-editor` (contenteditable) | Fill/Type |
| 短标题 | `input[placeholder*='填写短标题']` | Fill |
| 位置 | `input[placeholder*='搜索附近位置']` | 点开+Fill+选项 |
| 活动 | `input[placeholder*='搜索活动']` | 可选 |
| 定时发表 | `input.weui-desktop-form__radio` (不定时/定时) | 默认不定时 |
| 原创声明 | `input.ant-checkbox-input`（"声明后…原创标记"）| 可选勾选 |
| 发表 | `button.weui-desktop-btn_primary:has-text("发表")` | 提交 |
| 保存草稿 | `button.weui-desktop-btn_default:has-text("保存草稿")` | 存草稿 |

运行：`dotnet run -- real|dump|fill|extra`（默认用昭服 profile-6 + 项目里最小的剪辑视频，均不点发表）。

### 多步交互配方（extra 模式已实测 ✅）

- **封面上传**：`编辑`按钮 `.cover-preview-wrap .edit-btn` 被封面 `<img>` 遮挡 → 需 `Force=true` 点击 → 弹裁剪框 → 点 `上传封面` → 对 `input[type=file][accept*='image']`(取 `.Last`) `SetInputFilesAsync` → 点 `确认` → 出现「封面已更新」。直接对隐藏 input 设文件不触发 Vue，必须走弹框。
- **原创声明**：点 `.declare-original-checkbox label` → 弹「原创权益」对话框 → **先勾** `.weui-desktop-dialog .ant-checkbox`(我已阅读并同意)，`声明原创` 按钮才启用 → 点 `.weui-desktop-dialog button:has-text('声明原创')`。
- **点击链接**：点 `.post-with-link` 开下拉。挂载剧集选 **`视频号剧集`**（不是「小程序短剧」）。
- **挂载到视频号剧集**：选视频号剧集 → 点 `选择需要添加/关联的剧集` → 弹「选择需要关联的视频号剧集」对话框（异步加载+分页）→ 搜索框 `input[placeholder*='搜索内容']:visible`（必须 `:visible`，否则误配到位置行隐藏输入）→ 填**新剧名**（视频号注册名；原剧名搜不到）→ 结果 `GetByText(剧名)` 有隐藏+可见两个匹配，**遍历点第一个可见**的。
- 通用：weui 弹层元素常被兄弟/img 遮挡 → Force 点击或点可见文本；同名控件有隐藏副本 → `:visible` 或遍历可见项。

## 残留风险 / 下一步（P1 收尾）

A/B/C 已消除「.NET→CDP→上传」这一最大不确定性。迁移到最终架构前还需验证 **CEF 侧**：

1. **CEF 暴露 CDP 端点**：CefNet/CefGlue 启动时带 `--remote-debugging-port=<port>`（CefSettings
   `RemoteDebuggingPort`）。用本 spike 的思路把 `ConnectOverCDPAsync("http://localhost:<port>")`
   指向该 CEF 实例，重跑一次「setInputFiles + 读回」即可确认 CEF 的 CDP 覆盖 `DOM.setFileInputFiles`。
2. **每账号独立会话**：CEF 每账号一个 `CefRequestContext` + 独立 cache 路径（对应 persistent profile）。
3. **真实大文件上传**：channels 模式用几百 MB 的真实视频跑一遍，确认分片上传/进度文案与超时。

即：P1 的「能不能自动化」已 ✅；剩下的是「在内嵌 CEF 上重跑同一验证」——路径已由 C 打通。
