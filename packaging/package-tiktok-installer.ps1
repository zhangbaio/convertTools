[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version,
    [switch]$InstallPlaywrightChromium,
    [switch]$NoBundleDependencies,
    [switch]$NoBundleLocalAsrModels,
    [switch]$SkipInstallerCompile,
    [string]$InnoSetupCompiler,
    [string]$FfmpegDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
    # Offline Evergreen Standalone Installer (x64) permanent fwlink (~190MB, installs WebView2 with no
    # network needed on the target). This is the x64 "accept-and-download" link from the official
    # WebView2 download page. NOTE: do NOT use the bootstrapper link (fwlink 2124703) here - it is a
    # tiny online installer that would force new machines to have network access during setup.
    [string]$WebView2DownloadUrl = "https://go.microsoft.com/fwlink/?linkid=2124701"
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
$DependencyCacheDir = Join-Path $DependenciesDir "cache"
$ModelsDir = Join-Path $Root "models"
$VersionFile = Join-Path $Root "packaging\tiktok-installer-version.txt"
$BundleDependencies = -not $NoBundleDependencies
$BundleLocalAsrModels = $BundleDependencies -and -not $NoBundleLocalAsrModels
$ShouldAdvanceVersion = $false

if ([string]::IsNullOrWhiteSpace($Version)) {
    if (Test-Path -LiteralPath $VersionFile) {
        $Version = (Get-Content -LiteralPath $VersionFile -TotalCount 1).Trim()
    }
    else {
        $Version = "1.0.0"
    }

    $ShouldAdvanceVersion = $true
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid installer version '$Version'. Expected semantic version format like 1.0.0."
}

function Get-NextInstallerVersion {
    param([Parameter(Mandatory = $true)][string]$CurrentVersion)

    $parts = $CurrentVersion.Split('.')
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    return "$major.$($minor + 1).0"
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

function Remove-DependencyCacheDirectorySafe {
    param([Parameter(Mandatory = $true)][string]$Path)

    Assert-UnderDirectory -Path $Path -Parent $DependencyCacheDir
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

function Invoke-DownloadFile {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing
}

function Ensure-WebView2RuntimeInstaller {
    if (-not $BundleDependencies) {
        return
    }

    $installer = Join-Path $DependenciesDir "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
    if (Test-Path -LiteralPath $installer) {
        return
    }

    if ([string]::IsNullOrWhiteSpace($WebView2DownloadUrl)) {
        # No local file and no download URL: let the later hard check fail with guidance to place
        # the offline standalone installer manually.
        return
    }

    Invoke-DownloadFile -Url $WebView2DownloadUrl -Destination $installer
}

function Test-WebView2InstallerLooksStandalone {
    param([Parameter(Mandatory = $true)][string]$Installer)

    if (-not (Test-Path -LiteralPath $Installer)) {
        return $false
    }

    # The Evergreen bootstrapper is tiny and downloads the runtime on the target machine.
    # A standalone offline installer is much larger. Keep this conservative so packaging
    # can warn when a "full" installer still depends on target-machine network access.
    return (Get-Item -LiteralPath $Installer).Length -ge 20MB
}

function Ensure-FfmpegDependency {
    if (-not $BundleDependencies) {
        return
    }

    $targetDir = Join-Path $DependenciesDir "tools\$Runtime\ffmpeg"
    $ffmpeg = Join-Path $targetDir "ffmpeg.exe"
    $ffprobe = Join-Path $targetDir "ffprobe.exe"
    if ((Test-Path -LiteralPath $ffmpeg) -and (Test-Path -LiteralPath $ffprobe)) {
        return
    }

    $zipPath = Join-Path $DependencyCacheDir "ffmpeg-release-essentials.zip"
    $extractDir = Join-Path $DependencyCacheDir "ffmpeg-release-essentials"
    if (-not (Test-Path -LiteralPath $zipPath)) {
        Invoke-DownloadFile -Url $FfmpegDownloadUrl -Destination $zipPath
    }

    Remove-DependencyCacheDirectorySafe -Path $extractDir
    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force

    $extractedFfmpeg = Get-ChildItem -LiteralPath $extractDir -Recurse -File -Filter "ffmpeg.exe" |
        Select-Object -First 1
    $extractedFfprobe = Get-ChildItem -LiteralPath $extractDir -Recurse -File -Filter "ffprobe.exe" |
        Select-Object -First 1

    if (-not $extractedFfmpeg -or -not $extractedFfprobe) {
        throw "Downloaded ffmpeg archive did not contain ffmpeg.exe and ffprobe.exe: $zipPath"
    }

    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Copy-Item -LiteralPath $extractedFfmpeg.FullName -Destination $ffmpeg -Force
    Copy-Item -LiteralPath $extractedFfprobe.FullName -Destination $ffprobe -Force

    $license = Get-ChildItem -LiteralPath $extractDir -Recurse -File |
        Where-Object { $_.Name -match '^(LICENSE|COPYING|README)' } |
        Select-Object -First 1
    if ($license) {
        Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $targetDir $license.Name) -Force
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

function Assert-AnyPath {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$Candidates
    )

    if (-not (Test-AnyPath -Candidates $Candidates)) {
        throw "$Name is not bundled. Expected one of: $($Candidates -join ', ')"
    }
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

function Install-PlaywrightChromium {
    param([Parameter(Mandatory = $true)][string]$BrowserRoot)

    $playwrightScript = Join-Path $PublishDir "playwright.ps1"
    if (-not (Test-Path -LiteralPath $playwrightScript)) {
        throw "playwright.ps1 was not found in the publish directory; Chromium cannot be bundled."
    }

    New-Item -ItemType Directory -Force -Path $BrowserRoot | Out-Null
    $oldBrowserPath = $env:PLAYWRIGHT_BROWSERS_PATH
    try {
        $env:PLAYWRIGHT_BROWSERS_PATH = $BrowserRoot
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

function Get-PlaywrightBrowserRevision {
    param([Parameter(Mandatory = $true)][string]$BrowserName)

    $browsersJson = Join-Path $PublishDir ".playwright\package\browsers.json"
    if (-not (Test-Path -LiteralPath $browsersJson)) {
        return $null
    }

    try {
        $manifest = Get-Content -LiteralPath $browsersJson -Raw | ConvertFrom-Json
        $browser = $manifest.browsers |
            Where-Object { $_.name -eq $BrowserName } |
            Select-Object -First 1
        if ($null -eq $browser) {
            return $null
        }

        return $browser.revision
    }
    catch {
        return $null
    }
}

function Get-PlaywrightRuntimeDirectoryNames {
    $entries = @(
        @{ Name = "chromium"; Prefix = "chromium" },
        @{ Name = "chromium-headless-shell"; Prefix = "chromium_headless_shell" },
        @{ Name = "ffmpeg"; Prefix = "ffmpeg" },
        @{ Name = "winldd"; Prefix = "winldd" }
    )

    foreach ($entry in $entries) {
        $revision = Get-PlaywrightBrowserRevision -BrowserName $entry["Name"]
        if (-not [string]::IsNullOrWhiteSpace($revision)) {
            $prefix = $entry["Prefix"]
            "$prefix-$revision"
        }
    }
}

function Test-PlaywrightChromiumBundle {
    param([string]$BrowserRoot)

    if ([string]::IsNullOrWhiteSpace($BrowserRoot) -or -not (Test-Path -LiteralPath $BrowserRoot)) {
        return $false
    }

    $chromiumRevision = Get-PlaywrightBrowserRevision -BrowserName "chromium"
    $headlessRevision = Get-PlaywrightBrowserRevision -BrowserName "chromium-headless-shell"
    if (-not [string]::IsNullOrWhiteSpace($chromiumRevision) -and
        -not [string]::IsNullOrWhiteSpace($headlessRevision)) {
        $requiredFiles = @(
            (Join-Path $BrowserRoot "chromium-$chromiumRevision\chrome-win\chrome.exe"),
            (Join-Path $BrowserRoot "chromium_headless_shell-$headlessRevision\chrome-win\headless_shell.exe")
        )
        return (Test-Path -LiteralPath $requiredFiles[0]) -and (Test-Path -LiteralPath $requiredFiles[1])
    }

    $chromium = Get-ChildItem -LiteralPath $BrowserRoot -Directory -Filter "chromium-*" -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "chrome-win\chrome.exe") } |
        Select-Object -First 1
    $headless = Get-ChildItem -LiteralPath $BrowserRoot -Directory -Filter "chromium_headless_shell-*" -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "chrome-win\headless_shell.exe") } |
        Select-Object -First 1

    return $null -ne $chromium -and $null -ne $headless
}

function Copy-PlaywrightRuntimeFromLocalCache {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $sourceFull = [System.IO.Path]::GetFullPath($Source)
    $destinationFull = [System.IO.Path]::GetFullPath($Destination)
    if ($sourceFull.Equals($destinationFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $runtimeDirs = @(Get-PlaywrightRuntimeDirectoryNames)
    if ($runtimeDirs.Count -eq 0) {
        Copy-DirectoryContents -Source $Source -Destination $Destination
        return
    }

    foreach ($runtimeDir in $runtimeDirs) {
        $sourceDir = Join-Path $Source $runtimeDir
        if (Test-Path -LiteralPath $sourceDir) {
            Copy-Item -LiteralPath $sourceDir -Destination $Destination -Recurse -Force
        }
    }

    $linksDir = Join-Path $Source ".links"
    if (Test-Path -LiteralPath $linksDir) {
        Copy-Item -LiteralPath $linksDir -Destination $Destination -Recurse -Force
    }
}

function Resolve-LocalPlaywrightBrowserRoot {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:PLAYWRIGHT_BROWSERS_PATH)) {
        $candidates += $env:PLAYWRIGHT_BROWSERS_PATH
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates += (Join-Path $env:LOCALAPPDATA "ms-playwright")
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $candidates += (Join-Path $env:USERPROFILE "AppData\Local\ms-playwright")
        $candidates += (Join-Path $env:USERPROFILE ".cache\ms-playwright")
    }
    if (-not [string]::IsNullOrWhiteSpace($env:HOME)) {
        $candidates += (Join-Path $env:HOME "AppData\Local\ms-playwright")
        $candidates += (Join-Path $env:HOME ".cache\ms-playwright")
    }

    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        try {
            $fullPath = [System.IO.Path]::GetFullPath($candidate)
        }
        catch {
            continue
        }

        if ($seen.Add($fullPath) -and (Test-PlaywrightChromiumBundle -BrowserRoot $fullPath)) {
            return $fullPath
        }
    }

    return $null
}

function Ensure-PlaywrightChromiumCached {
    param(
        [Parameter(Mandatory = $true)][string]$CacheRoot,
        [switch]$AllowInstall
    )

    if (Test-PlaywrightChromiumBundle -BrowserRoot $CacheRoot) {
        Write-Host "Using cached Playwright Chromium: $CacheRoot"
        return
    }

    $localPlaywright = Resolve-LocalPlaywrightBrowserRoot
    if (-not [string]::IsNullOrWhiteSpace($localPlaywright)) {
        Write-Host "Copying local Playwright Chromium from: $localPlaywright"
        Copy-PlaywrightRuntimeFromLocalCache -Source $localPlaywright -Destination $CacheRoot
        if (Test-PlaywrightChromiumBundle -BrowserRoot $CacheRoot) {
            Write-Host "Cached Playwright Chromium is ready: $CacheRoot"
            return
        }
    }

    if ($AllowInstall) {
        Write-Host "Installing Playwright Chromium into dependency cache: $CacheRoot"
        Install-PlaywrightChromium -BrowserRoot $CacheRoot
    }
}

Write-Host "Packaging TikTokPublisher $Version ($Runtime, $Configuration)"
if ($BundleDependencies) {
    Write-Host "Bundling runtime dependencies: .NET self-contained, fonts/tools, ffmpeg, Playwright Chromium, WebView2 Runtime"
}
else {
    Write-Warning "Dependency bundling is disabled. The installer may require target machines to install dependencies separately."
}
if ($BundleLocalAsrModels) {
    Write-Host "Bundling local ASR models from: $ModelsDir"
}
elseif ($NoBundleLocalAsrModels) {
    Write-Warning "Local ASR model bundling is disabled. The installed app will require users to configure a local model directory."
}

New-Item -ItemType Directory -Force -Path $ArtifactsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $DependenciesDir | Out-Null
New-Item -ItemType Directory -Force -Path $DependencyCacheDir | Out-Null
Ensure-WebView2RuntimeInstaller
Ensure-FfmpegDependency
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

$publishTools = Join-Path $PublishDir "tools"
$repoFonts = Join-Path $Root "src\ShortDrama\tools\fonts"
Copy-DirectoryContents -Source $repoFonts -Destination (Join-Path $publishTools "fonts")

$extraTools = Join-Path $DependenciesDir "tools"
Copy-DirectoryContents -Source $extraTools -Destination $publishTools

$publishModels = Join-Path $PublishDir "models"
if ($BundleLocalAsrModels) {
    if (-not (Test-Path -LiteralPath $ModelsDir)) {
        throw "Local ASR models were not found at $ModelsDir. Put sherpa-onnx models there, or pass -NoBundleLocalAsrModels."
    }

    Copy-DirectoryContents -Source $ModelsDir -Destination $publishModels
}
else {
    Write-Warning "Local ASR models are not bundled. Users must set ASR local model paths or place models under the installed app's models directory."
}

$cachedPlaywright = Join-Path $DependenciesDir "ms-playwright"
if ($BundleDependencies -or $InstallPlaywrightChromium) {
    Ensure-PlaywrightChromiumCached -CacheRoot $cachedPlaywright -AllowInstall:($BundleDependencies -or $InstallPlaywrightChromium)
}
if (Test-Path -LiteralPath $cachedPlaywright) {
    Copy-DirectoryContents -Source $cachedPlaywright -Destination (Join-Path $PublishDir "ms-playwright")
}

$ffmpegCandidates = @(
    (Join-Path $PublishDir "tools\$Runtime\ffmpeg\ffmpeg.exe"),
    (Join-Path $PublishDir "tools\$Runtime\ffmpeg\bin\ffmpeg.exe"),
    (Join-Path $PublishDir "tools\ffmpeg\ffmpeg.exe"),
    (Join-Path $PublishDir "tools\ffmpeg\bin\ffmpeg.exe"),
    (Join-Path $PublishDir "ffmpeg.exe")
)
$ffprobeCandidates = @(
    (Join-Path $PublishDir "tools\$Runtime\ffmpeg\ffprobe.exe"),
    (Join-Path $PublishDir "tools\$Runtime\ffmpeg\bin\ffprobe.exe"),
    (Join-Path $PublishDir "tools\ffmpeg\ffprobe.exe"),
    (Join-Path $PublishDir "tools\ffmpeg\bin\ffprobe.exe"),
    (Join-Path $PublishDir "ffprobe.exe")
)
if ($BundleDependencies) {
    Assert-AnyPath -Name "ffmpeg.exe" -Candidates $ffmpegCandidates
    Assert-AnyPath -Name "ffprobe.exe" -Candidates $ffprobeCandidates
}
elseif (-not (Test-AnyPath -Candidates $ffmpegCandidates)) {
    Write-Warning "ffmpeg.exe is not bundled. Put ffmpeg/ffprobe under packaging\dependencies\tools\$Runtime\ffmpeg before building a fully offline installer."
}

$webView2LoaderCandidates = @(
    (Join-Path $PublishDir "WebView2Loader.dll"),
    (Join-Path $PublishDir "runtimes\$Runtime\native\WebView2Loader.dll")
)
$webView2CoreCandidates = @(
    (Join-Path $PublishDir "Microsoft.Web.WebView2.Core.dll")
)
if ($BundleDependencies) {
    Assert-AnyPath -Name "WebView2Loader.dll" -Candidates $webView2LoaderCandidates
    Assert-AnyPath -Name "Microsoft.Web.WebView2.Core.dll" -Candidates $webView2CoreCandidates
}

$defaultParaformerDir = Join-Path $publishModels "sherpa-onnx-paraformer-zh-2023-09-14"
$localAsrModelCandidates = @(
    (Join-Path $defaultParaformerDir "model.int8.onnx"),
    (Join-Path $defaultParaformerDir "model.onnx"),
    (Join-Path $publishModels "model.int8.onnx"),
    (Join-Path $publishModels "model.onnx")
)
$localAsrTokensCandidates = @(
    (Join-Path $defaultParaformerDir "tokens.txt"),
    (Join-Path $publishModels "tokens.txt")
)
$localAsrVadCandidates = @(
    (Join-Path $defaultParaformerDir "silero_vad.onnx"),
    (Join-Path $publishModels "silero_vad.onnx")
)
if ($BundleLocalAsrModels) {
    Assert-AnyPath -Name "Local ASR Paraformer model" -Candidates $localAsrModelCandidates
    Assert-AnyPath -Name "Local ASR tokens.txt" -Candidates $localAsrTokensCandidates
    Assert-AnyPath -Name "Local ASR silero_vad.onnx" -Candidates $localAsrVadCandidates
}

$playwrightRoot = Join-Path $PublishDir "ms-playwright"
$hasPlaywrightChromium = Test-PlaywrightChromiumBundle -BrowserRoot $playwrightRoot
if ($BundleDependencies -and -not $hasPlaywrightChromium) {
    throw "Playwright Chromium is not bundled. The installer would require a browser download on the target machine."
}
elseif (-not $hasPlaywrightChromium) {
    Write-Warning "Playwright Chromium is not bundled. Use -InstallPlaywrightChromium, prefill packaging\dependencies\ms-playwright, or install Playwright browsers on the packaging machine."
}

$webView2Installer = Join-Path $DependenciesDir "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
$webView2DownloadPage = "https://developer.microsoft.com/microsoft-edge/webview2/"
if ($BundleDependencies) {
    # Hard stop: missing OR too small (the online bootstrapper) both fail the build, so we never
    # ship an installer that requires network access on the target machine.
    if (-not (Test-Path -LiteralPath $webView2Installer)) {
        throw "WebView2 Runtime offline installer is missing. Download the Evergreen Standalone Installer (x64) to '$webView2Installer' (download page $webView2DownloadPage), or pass its direct URL via -WebView2DownloadUrl, then re-run packaging."
    }
    if (-not (Test-WebView2InstallerLooksStandalone -Installer $webView2Installer)) {
        $webView2SizeMb = [math]::Round((Get-Item -LiteralPath $webView2Installer).Length / 1MB, 1)
        throw "WebView2 installer is only $webView2SizeMb MB, which looks like the online Evergreen bootstrapper and would force new machines to have network access during install. Replace it with the offline Evergreen Standalone Installer (x64) (~130MB+). Download page: $webView2DownloadPage"
    }
}
elseif (-not (Test-Path -LiteralPath $webView2Installer)) {
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

if ($ShouldAdvanceVersion) {
    $nextVersion = Get-NextInstallerVersion -CurrentVersion $Version
    Set-Content -LiteralPath $VersionFile -Value $nextVersion -Encoding ASCII
    Write-Host "Next installer version: $nextVersion"
}
