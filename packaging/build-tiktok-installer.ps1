[CmdletBinding()]
param(
    [string]$Version,
    [switch]$InstallPlaywrightChromium,
    [switch]$NoBundleDependencies,
    [switch]$SkipInstallerCompile
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackageScript = Join-Path $ScriptDir "package-tiktok-installer.ps1"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = "1.0.$(Get-Date -Format 'yyyyMMdd').0"
}

$arguments = @(
    "-Version", $Version
)

if ($InstallPlaywrightChromium) {
    $arguments += "-InstallPlaywrightChromium"
}

if ($NoBundleDependencies) {
    $arguments += "-NoBundleDependencies"
}

if ($SkipInstallerCompile) {
    $arguments += "-SkipInstallerCompile"
}

Write-Host "Building TikTok installer version $Version"
& $PackageScript @arguments
