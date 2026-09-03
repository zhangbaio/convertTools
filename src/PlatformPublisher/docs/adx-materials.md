# ADX 素材下载与视频号发布

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

## 验收建议

首次真实账号验收先选择 1–2 条素材并保持默认“保存草稿”。确认视频、封面、描述、挂载剧集以及批次清单状态正确后，再通过视频号发表配置将终态改为直接发表。
