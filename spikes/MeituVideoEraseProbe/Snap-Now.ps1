$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SnapNative {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
$proc = Get-Process -Name MTXXVideo,XiuXiu -ErrorAction SilentlyContinue
$proc | ForEach-Object { Write-Host ("proc {0} {1} title={2}" -f $_.Id, $_.ProcessName, $_.MainWindowTitle) }
$want = @($proc | ForEach-Object { $_.Id })
$script:rows = @()
$enum = [SnapNative+EnumProc]{
  param($h, $l)
  $procId = [uint32]0
  [void][SnapNative]::GetWindowThreadProcessId($h, [ref]$procId)
  if ($want -notcontains [int]$procId) { return $true }
  if (-not [SnapNative]::IsWindowVisible($h)) { return $true }
  $title = New-Object System.Text.StringBuilder 256
  $cls = New-Object System.Text.StringBuilder 128
  [void][SnapNative]::GetWindowText($h, $title, 256)
  [void][SnapNative]::GetClassName($h, $cls, 128)
  $r = New-Object SnapNative+RECT
  [void][SnapNative]::GetWindowRect($h, [ref]$r)
  $w = $r.Right - $r.Left
  $hh = $r.Bottom - $r.Top
  if ($w -gt 200 -and $hh -gt 200) {
    $script:rows += [pscustomobject]@{ Hwnd = $h; Title = $title.ToString(); Class = $cls.ToString(); L = $r.Left; T = $r.Top; W = $w; H = $hh }
  }
  return $true
}
[void][SnapNative]::EnumWindows($enum, [IntPtr]::Zero)
$script:rows | ForEach-Object { Write-Host ("win {0} {1}x{2} @{3},{4} {5}" -f $_.Title, $_.W, $_.H, $_.L, $_.T, $_.Class) }
$target = $script:rows | Where-Object { $_.Title -like "*视频*" } | Select-Object -First 1
if ($null -eq $target) { $target = $script:rows | Select-Object -First 1 }
if ($null -eq $target) { throw "no window" }
[void][SnapNative]::ShowWindow($target.Hwnd, 9)
[void][SnapNative]::SetForegroundWindow($target.Hwnd)
Start-Sleep -Milliseconds 400
$r = New-Object SnapNative+RECT
[void][SnapNative]::GetWindowRect($target.Hwnd, [ref]$r)
$bmp = New-Object System.Drawing.Bitmap ($r.Right-$r.Left), ($r.Bottom-$r.Top)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$shot = Join-Path $PSScriptRoot "out\foreground.png"
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "shot=$shot"

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "MainWidget")
$main = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$names = New-Object System.Collections.Generic.List[string]
$stack = New-Object System.Collections.Generic.Stack[object]
$stack.Push($main)
$guard = 0
while ($stack.Count -gt 0 -and $guard -lt 400) {
  $guard++
  $node = $stack.Pop()
  try { $n = [string]$node.Current.Name; if ($n) { [void]$names.Add($n) } } catch {}
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  $child = $walker.GetFirstChild($node)
  $k = 0
  while ($null -ne $child -and $k -lt 40) {
    $stack.Push($child)
    $child = $walker.GetNextSibling($child)
    $k++
  }
}
Write-Host ("names=" + (($names | Select-Object -Unique) -join " | "))
