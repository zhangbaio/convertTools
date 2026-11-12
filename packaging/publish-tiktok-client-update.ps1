# 把 Velopack 产物直接传到管理系统签发的阿里云 OSS 地址，再发布为当前版本。
# 密钥留在管理系统，本脚本只拿两小时有效的上传地址。
# 先在发布目录生成升级包，例如：
#   vpk pack --packId TikTok.ShortDramaAssistant --packVersion 3.24.0 --packDir <publish目录> --mainExe TikTokPublisher.Desktop.exe --outputDir <本脚本的 ReleaseDir>
param(
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$Username,
    [Parameter(Mandatory = $true)][string]$Password,
    [Parameter(Mandatory = $true)][string]$ReleaseDir,
    [string]$Notes = "",
    [string]$Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ServerUrl = $ServerUrl.TrimEnd("/")
$releaseRoot = [System.IO.Path]::GetFullPath($ReleaseDir)
if (-not (Test-Path -LiteralPath $releaseRoot)) {
    throw "发布目录不存在：$releaseRoot"
}

$feedFile = Join-Path $releaseRoot "releases.win.json"
if (-not (Test-Path -LiteralPath $feedFile)) {
    throw "发布目录里没有 releases.win.json。先用 vpk pack 生成升级包。"
}

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-WebRequest -Uri "$ServerUrl/login" -Method POST -WebSession $session -Body @{
    username = $Username
    password = $Password
} | Out-Null
try {
    Invoke-RestMethod -Uri "$ServerUrl/api/client-update/settings" -WebSession $session | Out-Null
}
catch {
    throw "登录管理系统失败，请确认管理员账号。"
}

function Send-ReleaseFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    $name = [System.IO.Path]::GetFileName($Path)
    $ticketResponse = Invoke-RestMethod -Uri "$ServerUrl/api/client-update/upload-ticket" -Method POST -WebSession $session -ContentType "application/json" -Body (@{ fileName = $name } | ConvertTo-Json)
    Write-Host "上传 $name"
    Invoke-WebRequest -Uri $ticketResponse.url -Method PUT -Headers @{ "Content-Type" = $ticketResponse.contentType } -InFile $Path | Out-Null
}

$files = @(Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object {
    $_.Extension -in ".nupkg", ".exe"
})
foreach ($file in $files) {
    Send-ReleaseFile -Path $file.FullName
}
Send-ReleaseFile -Path $feedFile

if ([string]::IsNullOrWhiteSpace($Version)) {
    $feed = Get-Content -LiteralPath $feedFile -Raw | ConvertFrom-Json
    $full = @($feed.Assets) | Where-Object { $_.Type -eq "Full" } | Select-Object -First 1
    if ($null -eq $full) {
        throw "releases.win.json 里没有完整包版本。"
    }
    $Version = [string]$full.Version
}

$setup = $files | Where-Object { $_.Name -like "*Setup.exe" } | Select-Object -First 1
if ($null -eq $setup) {
    $setup = $files | Where-Object { $_.Extension -eq ".exe" } | Select-Object -First 1
}
$setupName = if ($null -eq $setup) { "" } else { $setup.Name }
$published = Invoke-RestMethod -Uri "$ServerUrl/api/client-update/releases" -Method POST -WebSession $session -ContentType "application/json" -Body (@{
    version = $Version
    notes = $Notes
    setupFile = $setupName
} | ConvertTo-Json)
Write-Host "已发布 $($published.version)，客户端下载目录：$($published.feedUrl)"
