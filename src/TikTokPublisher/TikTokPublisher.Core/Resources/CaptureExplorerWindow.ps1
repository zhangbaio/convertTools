param(
    [Parameter(Mandatory=$true)][string]$TargetPath,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [ValidateSet('Details','LargeIcons')][string]$View = 'Details'
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ExplorerCaptureNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
}
'@

$resolved = [IO.Path]::GetFullPath($TargetPath).TrimEnd('\')
$before = @{}
$shell = New-Object -ComObject Shell.Application
foreach ($item in @($shell.Windows())) { $before[[int64]$item.HWND] = $true }
Start-Process explorer.exe -ArgumentList @('/n,', $resolved)
$window = $null
for ($attempt = 0; $attempt -lt 60 -and $null -eq $window; $attempt++) {
    Start-Sleep -Milliseconds 200
    foreach ($candidate in @($shell.Windows())) {
        try {
            $location = ([uri]::UnescapeDataString([string]$candidate.LocationURL) -replace '^file:///', '') -replace '/', '\'
            if ([IO.Path]::GetFullPath($location).TrimEnd('\') -ieq $resolved -and -not $before.ContainsKey([int64]$candidate.HWND)) {
                $window = $candidate
                break
            }
        } catch {}
    }
}
if ($null -eq $window) { throw "Explorer window not found: $resolved" }

try {
    $hwnd = [IntPtr][int64]$window.HWND
    [ExplorerCaptureNative]::ShowWindow($hwnd, 9) | Out-Null
    [ExplorerCaptureNative]::MoveWindow($hwnd, 80, 60, 1280, 820, $true) | Out-Null
    [ExplorerCaptureNative]::SetForegroundWindow($hwnd) | Out-Null
    try {
        if ($View -eq 'LargeIcons') {
            $window.Document.CurrentViewMode = 1
            $window.Document.IconSize = 96
        } else {
            $window.Document.CurrentViewMode = 4
        }
    } catch {}
    Start-Sleep -Milliseconds 1200

    $rect = New-Object ExplorerCaptureNative+RECT
    if (-not [ExplorerCaptureNative]::GetWindowRect($hwnd, [ref]$rect)) { throw 'Cannot read Explorer window bounds' }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object Drawing.Bitmap($width, $height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $hdc = $graphics.GetHdc()
        try {
            if (-not [ExplorerCaptureNative]::PrintWindow($hwnd, $hdc, 2)) { throw 'PrintWindow capture failed' }
        } finally {
            $graphics.ReleaseHdc($hdc)
            $graphics.Dispose()
        }
        # Preserve the complete Explorer window. In particular, keep the address bar
        # so the captured directory can be verified from the screenshot itself.
        $outputFull = [IO.Path]::GetFullPath($OutputPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFull)) | Out-Null
        $bitmap.Save($outputFull, [Drawing.Imaging.ImageFormat]::Png)
        Write-Output $outputFull
    } finally {
        $bitmap.Dispose()
    }
} finally {
    try { $window.Quit() } catch {}
}
