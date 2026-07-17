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

$arguments = @{}

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $arguments.Version = $Version
}

if ($InstallPlaywrightChromium) {
    $arguments.InstallPlaywrightChromium = $true
}

if ($NoBundleDependencies) {
    $arguments.NoBundleDependencies = $true
}

if ($SkipInstallerCompile) {
    $arguments.SkipInstallerCompile = $true
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    Write-Host "Building TikTok installer using packaging\tiktok-installer-version.txt"
}
else {
    Write-Host "Building TikTok installer version $Version"
}
& $PackageScript @arguments
