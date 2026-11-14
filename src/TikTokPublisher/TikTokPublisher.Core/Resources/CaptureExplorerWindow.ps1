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
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetScrollInfo(IntPtr hWnd, int bar, ref SCROLLINFO scrollInfo);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumChildProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static EnumChildProc _enumWindowsCallback;
    public static IntPtr[] FindCabinetWindows() {
        var found = new System.Collections.Generic.List<IntPtr>();
        _enumWindowsCallback = delegate(IntPtr window, IntPtr parameter) {
            if (!IsWindowVisible(window)) return true;
            var className = new System.Text.StringBuilder(128);
            GetClassName(window, className, className.Capacity);
            var name = className.ToString();
            if (name == "CabinetWClass" || name == "ExploreWClass") found.Add(window);
            return true;
        };
        EnumWindows(_enumWindowsCallback, IntPtr.Zero);
        return found.ToArray();
    }

    public static void RequestClose(IntPtr window) {
        if (window == IntPtr.Zero) return;
        PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }

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

# Keep GetWindowRect, PrintWindow and crop coordinates in the same physical-pixel
# coordinate space on computers using 125%-300% display scaling.
try { [ExplorerCaptureNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch {}

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

function Open-ExplorerAtPath {
    param([string]$Arguments)

    # Pass one already-quoted argument string. Start-Process -ArgumentList joins
    # array items with spaces and lets Explorer split the path on the next comma.
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = Join-Path $env:SystemRoot 'explorer.exe'
    $startInfo.UseShellExecute = $false
    $startInfo.Arguments = $Arguments
    [void][System.Diagnostics.Process]::Start($startInfo)
}

function Get-FreshExplorerShell {
    return (New-Object -ComObject Shell.Application)
}

function Test-WindowPathMatches {
    param([IntPtr]$WindowHandle, [string]$ExpectedPath)

    # Used only when Shell.Windows() is stale. A full UI Automation walk is slower
    # than the COM location check, so the caller invokes it a few times, not per poll.
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
        if ($null -eq $root) { return $false }
        $elements = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $elements) {
            try {
                $patternObject = $null
                if (-not $element.TryGetCurrentPattern(
                        [System.Windows.Automation.ValuePattern]::Pattern,
                        [ref]$patternObject)) {
                    continue
                }
                $value = ([System.Windows.Automation.ValuePattern]$patternObject).Current.Value
                if ([string]::IsNullOrWhiteSpace($value)) { continue }
                if ($value -notmatch '^(file:|[A-Za-z]:\\|\\\\)') { continue }
                $location = Convert-ExplorerLocationUrlToPath $value
                if ([string]::IsNullOrWhiteSpace($location)) {
                    try {
                        $location = [IO.Path]::GetFullPath($value.Trim().Trim('"')).TrimEnd('\')
                    } catch {
                        continue
                    }
                }
                if ($location -ieq $ExpectedPath) { return $true }
            } catch {}
        }
    } catch {}
    return $false
}

function Close-ExplorerWindowsOpenedByCapture {
    param($KeepHwnd, $KnownWindows, $KnownCabinets)

    $keep = if ($null -eq $KeepHwnd) { [int64]0 } else { [int64]$KeepHwnd }
    try {
        $cleanupShell = Get-FreshExplorerShell
        foreach ($candidate in @($cleanupShell.Windows())) {
            try {
                $candidateHwnd = [int64]$candidate.HWND
                if ($KnownWindows.ContainsKey($candidateHwnd)) { continue }
                if ($candidateHwnd -eq $keep) { continue }
                $candidate.Quit()
            } catch {}
        }
    } catch {}

    foreach ($cabinet in @([ExplorerCaptureNative]::FindCabinetWindows())) {
        $cabinetHwnd = [int64]$cabinet
        if ($KnownCabinets.ContainsKey($cabinetHwnd)) { continue }
        if ($cabinetHwnd -eq $keep) { continue }
        [ExplorerCaptureNative]::RequestClose([IntPtr]$cabinetHwnd)
    }
}

$resolved = [IO.Path]::GetFullPath($TargetPath).TrimEnd('\')
# Explorer splits its own command line on commas. A title such as
# "My CEO, My All-consuming Love" must stay inside one quoted argument.
# The old unquoted "/separate, /e, <path>" form opens Documents instead, and the
# retries leave those extra Explorer processes behind until a reboot.
$quotedTarget = $resolved.Replace('"', '')
# Parentheses are required. The comma operator binds tighter than +, so an
# ungrouped array here collapses into one string and Explorer opens Documents.
$separateArguments = ('/separate,/e,"' + $quotedTarget + '"')
$pathArguments = ('"' + $quotedTarget + '"')

$before = @{}
$shell = Get-FreshExplorerShell
foreach ($item in @($shell.Windows())) {
    try { $before[[int64]$item.HWND] = $true } catch {}
}
$beforeCabinets = @{}
foreach ($cabinet in @([ExplorerCaptureNative]::FindCabinetWindows())) {
    $beforeCabinets[[int64]$cabinet] = $true
}

Open-ExplorerAtPath $separateArguments
$window = $null
$ownsWindow = $false
$usedFallbackLaunch = $false
$seenLocations = @{}
for ($attempt = 0; $attempt -lt 60 -and $null -eq $window; $attempt++) {
    Start-Sleep -Milliseconds 200
    if (($attempt % 5) -eq 4) {
        $shell = Get-FreshExplorerShell
    }

    $existingWindow = $null
    foreach ($candidate in @($shell.Windows())) {
        try {
            $candidateHwnd = [int64]$candidate.HWND
            $rawLocation = [string]$candidate.LocationURL
            $location = Convert-ExplorerLocationUrlToPath $rawLocation
            $isNewWindow = -not $before.ContainsKey($candidateHwnd)
            if ($isNewWindow -and -not [string]::IsNullOrWhiteSpace($rawLocation)) {
                $seenLocations[$rawLocation] = $true
            }
            if ($location -ine $resolved) { continue }
            if ($isNewWindow) {
                $window = $candidate
                $ownsWindow = $true
                break
            }
            if ($null -eq $existingWindow) { $existingWindow = $candidate }
        } catch {}
    }

    # Shell.Windows() can stop listing new Explorer windows until Explorer itself
    # is restarted. The folder window is still on screen; read its address directly.
    if ($null -eq $window -and ($attempt -eq 15 -or $attempt -eq 35 -or $attempt -eq 55)) {
        foreach ($cabinet in @([ExplorerCaptureNative]::FindCabinetWindows())) {
            $cabinetHwnd = [int64]$cabinet
            if ($beforeCabinets.ContainsKey($cabinetHwnd)) { continue }
            if (-not (Test-WindowPathMatches -WindowHandle ([IntPtr]$cabinetHwnd) -ExpectedPath $resolved)) {
                continue
            }
            $matchedCom = $null
            foreach ($candidate in @($shell.Windows())) {
                try {
                    if ([int64]$candidate.HWND -eq $cabinetHwnd) { $matchedCom = $candidate; break }
                } catch {}
            }
            if ($null -ne $matchedCom) {
                $window = $matchedCom
            } else {
                $window = [pscustomobject]@{ HWND = $cabinetHwnd }
            }
            $ownsWindow = $true
            break
        }
    }

    # Some Windows 11 configurations always reuse a tab. Give the dedicated-window
    # launch time to appear before accepting a pre-existing matching window.
    if ($null -eq $window -and $attempt -ge 30 -and $null -ne $existingWindow) {
        $window = $existingWindow
        $ownsWindow = $false
    }

    # /separate is ignored on some builds and never creates a window. Open the
    # folder once more, but only when the first launch produced no new window.
    if ($null -eq $window -and -not $usedFallbackLaunch -and $attempt -ge 30) {
        $newCabinetCount = 0
        foreach ($cabinet in @([ExplorerCaptureNative]::FindCabinetWindows())) {
            if (-not $beforeCabinets.ContainsKey([int64]$cabinet)) { $newCabinetCount++ }
        }
        if ($newCabinetCount -eq 0) {
            $usedFallbackLaunch = $true
            Open-ExplorerAtPath $pathArguments
        }
    }
}
if ($null -eq $window) {
    Close-ExplorerWindowsOpenedByCapture -KeepHwnd $null -KnownWindows $before -KnownCabinets $beforeCabinets
    $detail = if ($seenLocations.Count -eq 0) {
        'no new Explorer window registered'
    } else {
        ($seenLocations.Keys -join '; ')
    }
    throw "Explorer window not found: $resolved ($detail)"
}

try {
    $hwnd = [IntPtr][int64]$window.HWND
    $windowReady = $false
    $lastWidth = 0
    $lastHeight = 0
    for ($boundsAttempt = 0; $boundsAttempt -lt 12; $boundsAttempt++) {
        # Restore both classic Explorer windows and WinUI tab-host windows before
        # moving them. A minimized window otherwise reports bounds around 160x100.
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            $patternObject = $null
            if ($null -ne $root -and $root.TryGetCurrentPattern(
                    [System.Windows.Automation.WindowPattern]::Pattern,
                    [ref]$patternObject)) {
                ([System.Windows.Automation.WindowPattern]$patternObject).SetWindowVisualState(
                    [System.Windows.Automation.WindowVisualState]::Normal)
            }
        } catch {}
        [ExplorerCaptureNative]::ShowWindowAsync($hwnd, 9) | Out-Null
        [ExplorerCaptureNative]::ShowWindow($hwnd, 9) | Out-Null
        [ExplorerCaptureNative]::SetWindowPos(
            $hwnd, [IntPtr]::Zero, 80, 60, 1280, 820, 0x0040) | Out-Null # SWP_SHOWWINDOW
        [ExplorerCaptureNative]::MoveWindow($hwnd, 80, 60, 1280, 820, $true) | Out-Null
        [ExplorerCaptureNative]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 250

        $probeRect = New-Object ExplorerCaptureNative+RECT
        if ([ExplorerCaptureNative]::GetWindowRect($hwnd, [ref]$probeRect)) {
            $lastWidth = $probeRect.Right - $probeRect.Left
            $lastHeight = $probeRect.Bottom - $probeRect.Top
            if ($lastWidth -ge 1000 -and $lastHeight -ge 650 -and
                -not [ExplorerCaptureNative]::IsIconic($hwnd)) {
                $windowReady = $true
                break
            }
        }
    }
    if (-not $windowReady) {
        throw "Explorer window bounds did not stabilize: ${lastWidth}x${lastHeight}"
    }
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
        $desiredTop = [int][Math]::Round(72 * $dpiScale)
        $desiredBottom = [int][Math]::Round(28 * $dpiScale)
        # Always retain at least 500 pixels of content. This also protects unusual
        # mixed-DPI setups where Explorer and PowerShell report different scales.
        $cropBudget = [Math]::Max(0, $height - 500)
        $cropTop = [Math]::Min($desiredTop, [int][Math]::Floor($cropBudget * 0.72))
        $cropBottom = [Math]::Min($desiredBottom, $cropBudget - $cropTop)
        $cropRect = [Drawing.Rectangle]::new(
            $cropLeft,
            $cropTop,
            [Math]::Max(1, $width - $cropLeft),
            [Math]::Max(1, $height - $cropTop - $cropBottom))
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
    # Do close every other window this run opened, including a wrong folder
    # such as Documents, so failed retries cannot pile up Explorer processes.
    $keep = $null
    if (-not $ownsWindow -and $null -ne $window) { $keep = [int64]$window.HWND }
    Close-ExplorerWindowsOpenedByCapture -KeepHwnd $keep -KnownWindows $before -KnownCabinets $beforeCabinets
}
