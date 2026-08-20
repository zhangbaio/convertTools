param(
    [Parameter(Mandatory=$true)][string]$TargetPath,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [ValidateSet('Details','LargeIcons')][string]$View = 'Details'
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ExplorerCaptureNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
}
'@

function Reset-ExplorerNavigationPaneScroll {
    param([IntPtr]$WindowHandle)

    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
        if ($null -eq $root) { return }
        $rootBounds = $root.Current.BoundingRectangle
        $leftPaneLimit = $rootBounds.Left + ($rootBounds.Width * 0.35)
        $elements = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $elements) {
            try {
                $bounds = $element.Current.BoundingRectangle
                if ($bounds.Width -lt 80 -or $bounds.Height -lt 180 -or $bounds.Left -ge $leftPaneLimit) {
                    continue
                }

                $patternObject = $null
                if (-not $element.TryGetCurrentPattern(
                        [System.Windows.Automation.ScrollPattern]::Pattern,
                        [ref]$patternObject)) {
                    continue
                }
                $scrollPattern = [System.Windows.Automation.ScrollPattern]$patternObject
                if (-not $scrollPattern.Current.VerticallyScrollable) { continue }
                $scrollPattern.SetScrollPercent(
                    [System.Windows.Automation.ScrollPattern]::NoScroll,
                    0)
            } catch {}
        }
    } catch {}
}

function Wheel-ExplorerNavigationPaneToTop {
    param([IntPtr]$WindowHandle)

    $windowRect = New-Object ExplorerCaptureNative+RECT
    $originalCursor = New-Object ExplorerCaptureNative+POINT
    if (-not [ExplorerCaptureNative]::GetWindowRect($WindowHandle, [ref]$windowRect)) { return }
    if (-not [ExplorerCaptureNative]::GetCursorPos([ref]$originalCursor)) { return }
    try {
        # The navigation pane occupies the left side below the command bar. Wheel input
        # changes only its scroll position and does not select or open another directory.
        [ExplorerCaptureNative]::SetCursorPos($windowRect.Left + 110, $windowRect.Top + 260) | Out-Null
        for ($index = 0; $index -lt 48; $index++) {
            [ExplorerCaptureNative]::mouse_event(0x0800, 0, 0, 120, [UIntPtr]::Zero)
        }
    } finally {
        [ExplorerCaptureNative]::SetCursorPos($originalCursor.X, $originalCursor.Y) | Out-Null
    }
}

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
    Reset-ExplorerNavigationPaneScroll -WindowHandle $hwnd
    Start-Sleep -Milliseconds 400
    Reset-ExplorerNavigationPaneScroll -WindowHandle $hwnd
    Wheel-ExplorerNavigationPaneToTop -WindowHandle $hwnd
    Start-Sleep -Milliseconds 150

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
        # Keep the command bar, left navigation tree, file list and preview pane so the
        # screenshot visibly retains the Explorer directory context requested by users.
        # Only the window title/address/search area and bottom status strip are removed.
        $dpi = [ExplorerCaptureNative]::GetDpiForWindow($hwnd)
        if ($dpi -le 0) { $dpi = 96 }
        $dpiScale = $dpi / 96.0
        $cropLeft = 0
        $cropTop = [Math]::Min(
            [int][Math]::Round(72 * $dpiScale),
            [Math]::Max(0, $height - 1))
        $cropRect = [Drawing.Rectangle]::new(
            $cropLeft,
            $cropTop,
            [Math]::Max(1, $width - $cropLeft),
            [Math]::Max(1, $height - $cropTop - [int][Math]::Round(28 * $dpiScale)))
        $cropped = $bitmap.Clone($cropRect, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $outputFull = [IO.Path]::GetFullPath($OutputPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFull)) | Out-Null
        try {
            $cropped.Save($outputFull, [Drawing.Imaging.ImageFormat]::Png)
        } finally {
            $cropped.Dispose()
        }
        Write-Output $outputFull
    } finally {
        $bitmap.Dispose()
    }
} finally {
    try { $window.Quit() } catch {}
}
