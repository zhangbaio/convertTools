[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ProjectPath = Join-Path $Root "src\TikTokPublisher\TikTokPublisher.RegistrationPdf\TikTokPublisher.RegistrationPdf.csproj"
$PublishDir = Join-Path $Root "artifacts\publish\RegistrationPdf"
$ZipPath = Join-Path $Root "artifacts\RegistrationPdf-win-x64.zip"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed ($LASTEXITCODE): $FilePath $($Arguments -join ' ')"
    }
}

function Assert-RegistrationPdfPublished {
    param([Parameter(Mandatory = $true)][string]$PublishDirectory)

    $root = Join-Path $PublishDirectory "articles-of-incorporation"
    foreach ($file in @("template.pdf", "template-zh.pdf")) {
        $path = Join-Path $root $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Registration PDF template was not published: $path"
        }
    }

    $fontDir = Join-Path $root "fonts"
    New-Item -ItemType Directory -Force -Path $fontDir | Out-Null
    $windowsFonts = Join-Path $env:WINDIR "Fonts"
    foreach ($file in @("times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf", "simsun.ttc")) {
        $source = Join-Path $windowsFonts $file
        $destination = Join-Path $fontDir $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Cannot bundle registration PDF font '$file'. Install it at $source and re-run packaging."
        }
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }

    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory "RegistrationPdf.exe") -PathType Leaf)) {
        throw "RegistrationPdf.exe was not published."
    }
}

function Remove-UnusedRegistrationPdfPayload {
    param([Parameter(Mandatory = $true)][string]$PublishDirectory)

    foreach ($dir in @(".playwright", "templates", "timestamp-certificate", "fablecut", "tools")) {
        $path = Join-Path $PublishDirectory $dir
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }

    $unusedFiles = @(
        "onnxruntime.dll",
        "sherpa-onnx.dll",
        "sherpa-onnx-c-api.dll",
        "DocumentFormat.OpenXml.dll",
        "DocumentFormat.OpenXml.Framework.dll",
        "Microsoft.Playwright.dll",
        "Microsoft.Data.Sqlite.dll",
        "Microsoft.DiaSymReader.Native.amd64.dll",
        "e_sqlite3.dll",
        "SQLitePCLRaw.batteries_v2.dll",
        "SQLitePCLRaw.core.dll",
        "SQLitePCLRaw.provider.e_sqlite3.dll",
        "SixLabors.ImageSharp.dll",
        "SixLabors.ImageSharp.Drawing.dll",
        "ShortDrama.Core.dll",
        "ShortDrama.Infrastructure.dll",
        "ChannelsPublisher.Clip.dll",
        "mscordaccore.dll",
        "mscordbi.dll"
    )
    Get-ChildItem -LiteralPath $PublishDirectory -File -Filter "mscordaccore*.dll" |
        Remove-Item -Force
    foreach ($name in $unusedFiles) {
        $path = Join-Path $PublishDirectory $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File |
        Where-Object { $_.Extension -in ".pdb", ".xml" } |
        Remove-Item -Force
}

$dotnet = Join-Path $Root ".dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = "dotnet"
}

if (Test-Path -LiteralPath $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

Write-Host "Publishing RegistrationPdf ($Runtime, $Configuration)"
Invoke-Checked -FilePath $dotnet -Arguments @(
    "publish",
    $ProjectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", $PublishDir,
    "/p:PublishSingleFile=false",
    "/p:DebugType=none",
    "/p:DebugSymbols=false",
    "/p:DebuggerSupport=false",
    "/p:SatelliteResourceLanguages=en"
)

Assert-RegistrationPdfPublished -PublishDirectory $PublishDir
Remove-UnusedRegistrationPdfPayload -PublishDirectory $PublishDir
Assert-RegistrationPdfPublished -PublishDirectory $PublishDir

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($PublishDir, $ZipPath)
Write-Host "Portable package created: $ZipPath"
