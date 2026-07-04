[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version,
    [switch]$InstallPlaywrightChromium,
    [switch]$SkipInstallerCompile,
    [string]$InnoSetupCompiler
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ArtifactsRoot = Join-Path $Root "artifacts"
$PublishDir = Join-Path $ArtifactsRoot "publish\TikTokPublisher"
$InstallerDir = Join-Path $ArtifactsRoot "INSTALL"
$DependenciesDir = Join-Path $Root "packaging\dependencies"
$ProjectPath = Join-Path $Root "src\TikTokPublisher\TikTokPublisher.Desktop\TikTokPublisher.Desktop.csproj"
$InnoScript = Join-Path $Root "packaging\tiktok-publisher.iss"
$AppIconPath = Join-Path $Root "src\TikTokPublisher\TikTokPublisher.Desktop\Assets\tiktok-shortdrama-logo.ico"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = "1.0.$(Get-Date -Format 'yyyyMMdd').0"
}

function Assert-UnderDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    if (-not $fullPath.StartsWith($fullParent + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside packaging artifacts: $fullPath"
    }
}

function Remove-DirectorySafe {
    param([Parameter(Mandatory = $true)][string]$Path)

    Assert-UnderDirectory -Path $Path -Parent $ArtifactsRoot
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        return
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Resolve-DotNet {
    $localDotNet = Join-Path $Root ".dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $localDotNet) {
        return $localDotNet
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    throw "dotnet was not found. Install the .NET SDK or restore the local .dotnet toolchain."
}

function Resolve-InnoSetupCompiler {
    if (-not [string]::IsNullOrWhiteSpace($InnoSetupCompiler)) {
        if (Test-Path -LiteralPath $InnoSetupCompiler) {
            return [System.IO.Path]::GetFullPath($InnoSetupCompiler)
        }

        throw "ISCC.exe was not found at: $InnoSetupCompiler"
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @()
    $programFiles = [Environment]::GetEnvironmentVariable("ProgramFiles")
    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    if ($programFiles) {
        $candidates += Join-Path $programFiles "Inno Setup 6\ISCC.exe"
    }
    if ($programFilesX86) {
        $candidates += Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe"
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    return $null
}

function Test-AnyPath {
    param([string[]]$Candidates)

    foreach ($candidate in $Candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $true
        }
    }

    return $false
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
    }
}

Write-Host "Packaging TikTokPublisher $Version ($Runtime, $Configuration)"

New-Item -ItemType Directory -Force -Path $ArtifactsRoot | Out-Null
Remove-DirectorySafe -Path $PublishDir
Remove-DirectorySafe -Path $InstallerDir
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $InstallerDir | Out-Null

$dotnet = Resolve-DotNet
Invoke-Checked -FilePath $dotnet -Arguments @(
    "publish",
    $ProjectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", $PublishDir,
    "/p:PublishSingleFile=false",
    "/p:IncludeNativeLibrariesForSelfExtract=true"
)

$repoTools = Join-Path $Root "src\ShortDrama\tools"
$publishTools = Join-Path $PublishDir "tools"
Copy-DirectoryContents -Source $repoTools -Destination $publishTools

$extraTools = Join-Path $DependenciesDir "tools"
Copy-DirectoryContents -Source $extraTools -Destination $publishTools

$cachedPlaywright = Join-Path $DependenciesDir "ms-playwright"
if (Test-Path -LiteralPath $cachedPlaywright) {
    Copy-DirectoryContents -Source $cachedPlaywright -Destination (Join-Path $PublishDir "ms-playwright")
}

if ($InstallPlaywrightChromium) {
    $playwrightScript = Join-Path $PublishDir "playwright.ps1"
    if (-not (Test-Path -LiteralPath $playwrightScript)) {
        Write-Warning "playwright.ps1 was not found in the publish directory; Chromium was not downloaded."
    }
    else {
        $browserRoot = Join-Path $PublishDir "ms-playwright"
        New-Item -ItemType Directory -Force -Path $browserRoot | Out-Null
        $oldBrowserPath = $env:PLAYWRIGHT_BROWSERS_PATH
        try {
            $env:PLAYWRIGHT_BROWSERS_PATH = $browserRoot
            $runnerCommand = Get-Command pwsh -ErrorAction SilentlyContinue
            if (-not $runnerCommand) {
                $runnerCommand = Get-Command powershell.exe -ErrorAction Stop
            }
            $runner = $runnerCommand.Source

            if ([System.IO.Path]::GetFileName($runner).Equals("powershell.exe", [System.StringComparison]::OrdinalIgnoreCase)) {
                Invoke-Checked -FilePath $runner -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $playwrightScript, "install", "chromium")
            }
            else {
                Invoke-Checked -FilePath $runner -Arguments @("-NoProfile", "-File", $playwrightScript, "install", "chromium")
            }
        }
        finally {
            $env:PLAYWRIGHT_BROWSERS_PATH = $oldBrowserPath
        }
    }
}

$ffmpegCandidates = @(
    (Join-Path $PublishDir "tools\$Runtime\ffmpeg\ffmpeg.exe"),
    (Join-Path $PublishDir "tools\$Runtime\ffmpeg\bin\ffmpeg.exe"),
    (Join-Path $PublishDir "tools\ffmpeg\ffmpeg.exe"),
    (Join-Path $PublishDir "tools\ffmpeg\bin\ffmpeg.exe"),
    (Join-Path $PublishDir "ffmpeg.exe")
)
if (-not (Test-AnyPath -Candidates $ffmpegCandidates)) {
    Write-Warning "ffmpeg.exe is not bundled. Put ffmpeg/ffprobe under packaging\dependencies\tools\$Runtime\ffmpeg before building a fully offline installer."
}

$playwrightRoot = Join-Path $PublishDir "ms-playwright"
$chromiumDirs = @()
if (Test-Path -LiteralPath $playwrightRoot) {
    $chromiumDirs = @(Get-ChildItem -LiteralPath $playwrightRoot -Directory -Filter "chromium-*" -ErrorAction SilentlyContinue)
}
if ($chromiumDirs.Count -eq 0) {
    Write-Warning "Playwright Chromium is not bundled. Use -InstallPlaywrightChromium, or prefill packaging\dependencies\ms-playwright."
}

$webView2Installer = Join-Path $DependenciesDir "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
if (-not (Test-Path -LiteralPath $webView2Installer)) {
    Write-Warning "WebView2 Runtime installer is not bundled. Put MicrosoftEdgeWebView2RuntimeInstallerX64.exe under packaging\dependencies for new Windows machines without WebView2."
}

if ($SkipInstallerCompile) {
    Write-Host "Skipped Inno Setup compile. Published app: $PublishDir"
    exit 0
}

$iscc = Resolve-InnoSetupCompiler
if (-not $iscc) {
    throw "Inno Setup compiler (ISCC.exe) was not found. Install Inno Setup 6, or pass -InnoSetupCompiler <path>. Published app is ready at: $PublishDir"
}

$isccArgs = @(
    "/DAppVersion=$Version",
    "/DPublishDir=$PublishDir",
    "/DOutputDir=$InstallerDir"
)
if (Test-Path -LiteralPath $AppIconPath) {
    $isccArgs += "/DAppIconFile=$AppIconPath"
}
if (Test-Path -LiteralPath $webView2Installer) {
    $isccArgs += "/DWebView2Installer=$webView2Installer"
}
$isccArgs += $InnoScript

Invoke-Checked -FilePath $iscc -Arguments $isccArgs

$installerPath = Join-Path $InstallerDir "TikTokShortDramaUploader-Setup-$Version.exe"
Write-Host "Installer created: $installerPath"
