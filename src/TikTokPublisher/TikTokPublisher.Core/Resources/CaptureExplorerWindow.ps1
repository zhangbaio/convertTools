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
    public delegate bool EnumChildProc(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct SCROLLINFO {
        public uint Size;
        public uint Mask;
        public int Min;
        public int Max;
        public uint Page;
        public int Position;
        public int TrackPosition;
    }
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetScrollInfo(IntPtr hWnd, int bar, ref SCROLLINFO scrollInfo);

    public static IntPtr FindNavigationTree(IntPtr parent) {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, delegate(IntPtr child, IntPtr parameter) {
            var className = new System.Text.StringBuilder(128);
            GetClassName(child, className, className.Capacity);
            if (string.Equals(className.ToString(), "SysTreeView32", StringComparison.Ordinal)) {
                found = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool ScrollNavigationTreeToTop(IntPtr parent) {
        IntPtr tree = FindNavigationTree(parent);
        if (tree == IntPtr.Zero) return false;
        SendMessage(tree, 0x0115, new UIntPtr(6), IntPtr.Zero); // WM_VSCROLL / SB_TOP
        return true;
    }

    public static bool IsNavigationTreeAtTop(IntPtr parent) {
        IntPtr tree = FindNavigationTree(parent);
        if (tree == IntPtr.Zero) return false;
        var info = new SCROLLINFO {
            Size = (uint)Marshal.SizeOf(typeof(SCROLLINFO)),
            Mask = 0x0001 | 0x0004 // SIF_RANGE | SIF_POS
        };
        return GetScrollInfo(tree, 1, ref info) && info.Position <= info.Min;
    }
}
'@

function Reset-ExplorerNavigationPaneScroll {
    param([IntPtr]$WindowHandle)

    $nativeUpdated = [ExplorerCaptureNative]::ScrollNavigationTreeToTop($WindowHandle)
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
        if ($null -eq $root) { return $nativeUpdated }
        $rootBounds = $root.Current.BoundingRectangle
        $leftPaneLimit = $rootBounds.Left + ($rootBounds.Width * 0.32)
        $updated = $false
        $elements = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $elements) {
            try {
                $bounds = $element.Current.BoundingRectangle
                # Requiring the whole element to stay inside the left portion avoids
                # accidentally resetting the main file list, whose left edge can also
                # begin inside the former broad cutoff.
                if ($bounds.Width -lt 80 -or $bounds.Height -lt 180 -or
                    $bounds.Left -ge $leftPaneLimit -or $bounds.Right -gt $leftPaneLimit) {
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
                $updated = $true
            } catch {}
        }
        return ($nativeUpdated -or $updated)
    } catch {
        return $nativeUpdated
    }
}

function Test-ExplorerNavigationPaneAtTop {
    param([IntPtr]$WindowHandle)

    if ([ExplorerCaptureNative]::IsNavigationTreeAtTop($WindowHandle)) {
        return $true
    }

    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
        if ($null -eq $root) { return $false }
        $rootBounds = $root.Current.BoundingRectangle
        $leftPaneLimit = $rootBounds.Left + ($rootBounds.Width * 0.32)
        $foundScrollablePane = $false
        $elements = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $elements) {
            try {
                $bounds = $element.Current.BoundingRectangle
                if ($bounds.Width -lt 80 -or $bounds.Height -lt 180 -or
                    $bounds.Left -ge $leftPaneLimit -or $bounds.Right -gt $leftPaneLimit) {
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
                $foundScrollablePane = $true
                if ($scrollPattern.Current.VerticalScrollPercent -gt 0.5) {
                    return $false
                }
            } catch {}
        }
        return $foundScrollablePane
    } catch {
        return $false
    }
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
        $screenX = $windowRect.Left + 110
        $screenY = $windowRect.Top + 260
        [ExplorerCaptureNative]::SetCursorPos($screenX, $screenY) | Out-Null
        $point = New-Object ExplorerCaptureNative+POINT
        $point.X = $screenX
        $point.Y = $screenY
        $navigationHandle = [ExplorerCaptureNative]::WindowFromPoint($point)
        $wheelUp = [UIntPtr]([uint64](120 -shl 16))
        $screenCoordinates = [IntPtr](($screenY -shl 16) -bor ($screenX -band 0xFFFF))
        for ($index = 0; $index -lt 48; $index++) {
            # Direct delivery works for Explorer's WinUI/XAML navigation host even
            # when Windows foreground-lock rules reject SetForegroundWindow.
            if ($navigationHandle -ne [IntPtr]::Zero) {
                [ExplorerCaptureNative]::SendMessage(
                    $navigationHandle, 0x020A, $wheelUp, $screenCoordinates) | Out-Null
            }
            [ExplorerCaptureNative]::mouse_event(0x0800, 0, 0, 120, [UIntPtr]::Zero)
        }
    } finally {
        [ExplorerCaptureNative]::SetCursorPos($originalCursor.X, $originalCursor.Y) | Out-Null
    }
}

function Wait-ExplorerNavigationPaneAtTop {
    param([IntPtr]$WindowHandle)

    # Explorer expands and selects deep target folders asynchronously. That late
    # selection can undo an earlier scroll-to-top, especially for the first capture
    # in a fresh Explorer process. Require three consecutive top readings so the
    # navigation tree has settled before PrintWindow runs.
    $requiredStableSamples = 3
    $stableSamples = 0
    for ($attempt = 0; $attempt -lt 16; $attempt++) {
        $uiaReset = Reset-ExplorerNavigationPaneScroll -WindowHandle $WindowHandle
        if (-not $uiaReset -or -not (Test-ExplorerNavigationPaneAtTop -WindowHandle $WindowHandle)) {
            Wheel-ExplorerNavigationPaneToTop -WindowHandle $WindowHandle
        }
        Start-Sleep -Milliseconds 250

        if (Test-ExplorerNavigationPaneAtTop -WindowHandle $WindowHandle) {
            $stableSamples++
            if ($stableSamples -ge $requiredStableSamples) { return $true }
        } else {
            $stableSamples = 0
        }
    }

    # UI Automation support varies between Explorer builds. Keep the wheel fallback
    # for unsupported navigation panes, then allow capture rather than failing the
    # entire evidence-generation step.
    Wheel-ExplorerNavigationPaneToTop -WindowHandle $WindowHandle
    Start-Sleep -Milliseconds 500
    return $false
}

function Convert-ExplorerLocationUrlToPath {
    param([string]$LocationUrl)

    if ([string]::IsNullOrWhiteSpace($LocationUrl)) { return $null }
    try {
        $locationUri = [Uri]$LocationUrl
        if (-not $locationUri.IsFile) { return $null }

        # Uri.LocalPath handles both forms emitted by Shell.Application:
        #   file:///C:/folder      -> C:\folder
        #   file://server/share   -> \\server\share
        # Stripping only "file:///" turns a UNC URL into the relative path
        # "file:\\server\share", which can never match the requested directory.
        return [IO.Path]::GetFullPath($locationUri.LocalPath).TrimEnd('\')
    } catch {
        return $null
    }
}

$resolved = [IO.Path]::GetFullPath($TargetPath).TrimEnd('\')
$before = @{}
$shell = New-Object -ComObject Shell.Application
foreach ($item in @($shell.Windows())) { $before[[int64]$item.HWND] = $true }
Start-Process explorer.exe -ArgumentList @('/n,', $resolved)
$window = $null
$ownsWindow = $false
for ($attempt = 0; $attempt -lt 60 -and $null -eq $window; $attempt++) {
    Start-Sleep -Milliseconds 200
    foreach ($candidate in @($shell.Windows())) {
        try {
            $location = Convert-ExplorerLocationUrlToPath ([string]$candidate.LocationURL)
            if ($location -ieq $resolved) {
                $window = $candidate
                $ownsWindow = -not $before.ContainsKey([int64]$candidate.HWND)
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
    Wait-ExplorerNavigationPaneAtTop -WindowHandle $hwnd | Out-Null

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
    # Windows 11 may open the target in an existing Explorer window/tab. Do not
    # close a window that belonged to the user before this capture started.
    if ($ownsWindow) {
        try { $window.Quit() } catch {}
    }
}
