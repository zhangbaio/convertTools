# 红果 APP 下载维护索引

TikTok 助手里的「红果APP下载」（链路 id `hongguoapp`）按本目录的协议实现。代码在 `src/ShortDrama/ShortDrama.Infrastructure/Automation/`。设置页在 TikTok 助手的 **系统设置 → 数据链路与下载参数 → 红果APP下载**。

本目录文档于 2026-11-01 从 hongguo 仓库同步，方便以后改协议或解密时不用再翻另一个仓库。

| 文档 | 用途 |
| --- | --- |
| [红果免签名机接入-20261101.md](红果免签名机接入-20261101.md) | 搜索、剧集列表、链路 1 播放地址的请求合同 |
| [逆向复盘-spade解密-20260604.md](逆向复盘-spade解密-20260604.md) | `spade_a` 如何变成内容密钥，以及 CENC 解密怎么接到下载 |
| [VIDEO_DECRYPTION_RE.md](VIDEO_DECRYPTION_RE.md) | 解密的时间顺序记录。看结论时读文末接力指南，前面是已排除的弯路 |
| [REVERSE_ENGINEERING.md](REVERSE_ENGINEERING.md) | 早期抓包记录。不要按它去部署 unidbg 或模拟器签名 |

2026-06-29 的《红果脱机下载-端到端》把 unidbg 写成前置条件。那只对官方 App 的 `multi_video_model/preload/v1` 成立。当前这条下载不要走那条接口，也不要恢复签名机。

## 代码对照

| 步骤 | 类 | 说明 |
| --- | --- | --- |
| 搜索、剧集列表、漫剧上新、AI短剧上新 | `HongguoPureSign`、`HongguoAppDirectClient` | 进程内 PureSign。搜索设备号单独写在 `%LOCALAPPDATA%/TikTokPublisher/hongguoapp-device.json`，不要给它配播放安装号。上新走分类页，体裁分别是 `comic_series` 和 `ai_series`，排序 `online_time`，时间 `days_7` |
| 链路 1 播放地址 | `HongguoGuestPlayback` | 匿名 `device_register` 之后，无签名 POST `multi_video_model/v1` |
| 链路 2 播放地址 | `HongguoSignedPlayback` | 同一对播放设备号和安装号，PureSign 后再请求同一接口。没有分集画面时走 `fallback_api`，`key_seed` 密文要先解密。不跟随跳转 |
| 选择两条链路 | `HongguoAppDirectClient.GetVideoPlaybackAsync` | 设置里至少留一条。两条都开时先链路 1，失败再链路 2 |
| 下载解密 | `HongguoCdnDecryptor`、`HongguoSpadeKey` | 有 `spade_a` 时按密文下载再解密。优先 1080p / `bytevc1` |
| 设置持久化 | `ClientSettings.HongguoAppDeviceId`、`HongguoAppInstallId`、`HongguoAppStreamLink1`、`HongguoAppStreamLink2` | 播放身份在助手第一次启动时向 `device_register` 注册并写入系统设置，之后复用。清空两个号码并重新打开助手会重新注册 |

剧集 id 仍用 `HongguoHighCrypto.EncodeEpisodeId`（`hghigh_ep:`）。路由在 `DramaSourceRouter` 的 `SearchHongguoAppAsync`、`GetHongguoAppEpisodesAsync`、`GetHongguoAppVideoUrlAsync`。

## 不要一起改的路径

榜单仍走红果短剧助手账号，入口是 `DramaSourceRouter.ForHongguoApp` 和 `GetRankPageAsync`，背后是 `_hghighApiService`。漫剧上新和 AI短剧上新走 `HongguoAppDirectClient.GetNewReleaseAsync`，不按本地日期再滤掉没有上线时间的剧。`hghigh` 的 `/video/batch-parse` 也不属于这条客户端直连。

播放身份和搜索设备号是两套数字。播放请求把 `device_id` 和 `iid` 配错会得到空包。搜索可以只带自己的 `device_id`。

链路 1 的播放 query 版本是 `71332` / `7.1.3.32`，注册包版本是 `73932` / `7.3.9.32`。这个差异是故意的。`resolution` 里的 `*` 必须转成 `%2A`。PureSign 的 `Enc` 会把 `%2A` 变回 `*`，播放 query 不要用它。

## 许可

`HongguoPureSign`、`HongguoSm3` 来自 jackotom/hongguo-drama 的 `app-source.js` / `sm3.js`（GPL-3.0），经 sunnysky123/hongguo-web 传入。链路 2 的签名请求和媒体地址解密同样来自这份 `app-source.js`（hongguo-web `625e4e1` 的 `AppSource`）。匿名注册包络来自 tsukiyo-zero/hongguoTV 的 `DeviceRegister.kt`（GPL-3.0）。改这些文件时保留文件头里的来源说明，不要改成仓库根许可。
