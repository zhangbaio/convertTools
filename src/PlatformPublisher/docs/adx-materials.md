# ADX 素材下载与视频号、快手发布

## 使用入口

启动“云帆多平台发布助手”，进入“视频号 → 多账号素材发布”，选择左侧视频号账号和工作目录，然后点击“ADX素材下载”。

首次使用时填写 ADX 服务地址、账号、密码、查询数量和下载并发数，保存后点击“登录 ADX”。密码和浏览器登录态使用 Windows DPAPI 当前用户范围加密，不能在其他 Windows 用户下解密。

填写项目原剧名和新剧名后查询，勾选候选素材，可选择：

- “仅下载”：保存视频、封面、旁车和批次清单，不创建发布任务。
- “下载后自动发布”：先持久化 ADX 发布任务，再执行视频号发布；默认安全终态为“保存草稿”。

## 数据位置

ADX 全局配置保存在 `%LocalAppData%\YunfanPlatformPublisher\adx`：

- `settings.json`：非敏感配置。
- `password.dat`：DPAPI 加密密码。
- `auth-state.dat`：DPAPI 加密 Playwright storage state。

项目下载批次保持与云帆 Electron 版兼容：

```text
<workflow>\materials\adx\<yyyyMMddHHmm>\
├─ <新剧名>-TOP001-<materialId>.mp4
├─ <新剧名>-TOP001-<materialId>.cover.jpg
├─ <新剧名>-TOP001-<materialId>.publish.json
└─ .weixin-channels-adx-state.json
```

批次读取兼容 v1/v2；清单缺失时会根据视频和 `.publish.json` 旁车重建。发布结果写入 `publishByAccount[accountId].items[materialId]`。成功或已保存草稿的历史结果不会被后续失败重试覆盖。

## 从 Electron 云帆迁移

可以继续使用原工作目录中的 `materials\adx` 批次。Electron `safeStorage` 保存的密码和登录态不直接导入，需在本应用重新输入密码并登录一次。

## 快手分账个人版

进入“快手分账 · 个人”，先选择一个剧集项目，再使用顶部的“下载ADX素材”或“发布ADX素材”：

- 下载会复用同一套 ADX 账号、登录态和 `materials\adx` 批次目录。
- 发布会汇总当前项目的本地批次，缺失文件不可选，当前快手账号已发布的素材默认不选。
- 发布任务进入原有任务队列；执行时按新剧名精确定位短剧，填写宣发剪辑标题、类型、作者声明和封面，然后上传视频并发布。
- 快手发布状态以 `kuaishou-personal:<accountId>` 写入批次清单，与视频号账号状态隔离。
- 快手标题最长 20 个字符。应用会截断标题前缀并保留完整素材 ID，以便重试时安全识别平台历史记录。

发布前应确保项目存在 `快手竖屏海报.jpg`，或者在发布窗口选择 ADX 封面/指定单图封面。单个视频必须小于等于 1 GiB，格式为 MP4、MOV、OGG 或 WebM。

## 验收建议

首次真实账号验收先选择 1–2 条素材并保持默认“保存草稿”。确认视频、封面、描述、挂载剧集以及批次清单状态正确后，再通过视频号发表配置将终态改为直接发表。
