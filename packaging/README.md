# TikTok 短剧助手安装包

## 构建

构建机需要安装 .NET SDK 和 Inno Setup 6，然后运行：

```powershell
.\packaging\package-tiktok-installer.ps1
```

输出安装包：

```text
artifacts\INSTALL\TikTokShortDramaUploader-Setup-<version>.exe
```

只发布文件、不编译安装包：

```powershell
.\packaging\package-tiktok-installer.ps1 -SkipInstallerCompile
```

下载并打包 Playwright Chromium：

```powershell
.\packaging\package-tiktok-installer.ps1 -InstallPlaywrightChromium
```

## 安装体验

- 安装向导支持选择安装目录，默认安装到当前用户目录，不需要管理员权限。
- 安装向导有“重置本地数据”复选框，默认不勾选。
- 勾选重置时会清理当前用户的 `%USERPROFILE%\.tiktok_publisher` 中的账号、登录态、授权态等本地数据，但保留 `app.db` 和 `reports` 上传记录；不会删除工作目录中的短剧素材。

## 可随安装包携带的依赖

- .NET 运行时：脚本使用 `--self-contained true`，会打进发布目录。
- 字体：自动复制 `src\ShortDrama\tools\fonts` 到安装目录的 `tools\fonts`。
- ffmpeg/ffprobe：当前仓库没有内置二进制。需要离线运行时，把文件放到 `packaging\dependencies\tools\win-x64\ffmpeg\ffmpeg.exe` 和 `ffprobe.exe`，脚本会复制进安装包。
- Playwright Chromium：使用 `-InstallPlaywrightChromium` 下载到发布目录，或预先放到 `packaging\dependencies\ms-playwright`。
- WebView2 Runtime：把 `MicrosoftEdgeWebView2RuntimeInstallerX64.exe` 放到 `packaging\dependencies`，安装器会在目标机器缺少 WebView2 时静默安装。

`packaging\dependencies` 是本地缓存目录，默认不入库。
