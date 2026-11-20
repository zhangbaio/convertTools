[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0',
    [switch]$SkipInstallerCompile,
    [string]$InnoSetupCompiler
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$desktopDir = Join-Path $root 'src\PlatformPublisher\PlatformPublisher.Desktop'
$project = Join-Path $desktopDir 'PlatformPublisher.Desktop.csproj'
$icon = Join-Path $desktopDir 'Assets\yunfan-logo.ico'
$publishDir = Join-Path $root 'artifacts\publish\YunfanPlatformPublisher'
$installerDir = Join-Path $root 'artifacts\INSTALL'
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

Push-Location -LiteralPath $root
try {
    & $dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "Yunfan publish failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if ($SkipInstallerCompile) {
    return
}

if (-not $InnoSetupCompiler) {
    $InnoSetupCompiler = (Get-Command ISCC.exe -ErrorAction Stop).Source
}

$script = Join-Path $PSScriptRoot 'platform-publisher.iss'
& $InnoSetupCompiler "/DAppVersion=$Version" "/DPublishDir=$publishDir" "/DOutputDir=$installerDir" "/DAppIconFile=$icon" $script
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}
