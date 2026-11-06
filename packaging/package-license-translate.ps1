[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$Dotnet = Join-Path $Root ".dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $Dotnet)) {
    throw "Bundled .NET SDK was not found: $Dotnet"
}

$Project = Join-Path $Root "src\LicenseTranslate\LicenseTranslate.App\LicenseTranslate.App.csproj"
$PublishDir = Join-Path $Root "artifacts\publish\LicenseTranslate"
$ZipPath = Join-Path $Root "artifacts\LicenseTranslate-win-x64.zip"

if (Test-Path -LiteralPath $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path (Split-Path $ZipPath) | Out-Null

& $Dotnet publish $Project -c $Configuration -r $Runtime --self-contained true -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $PublishDir "执照翻译.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Published executable was not found: $exe"
}

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath
Write-Host "Portable package: $ZipPath"
