$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TaskNative {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
function Get-Main {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "MainWidget")
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}
function Dump-Names($el) {
  $names = New-Object System.Collections.Generic.List[string]
  $stack = New-Object System.Collections.Generic.Stack[object]
  $stack.Push(@{ El = $el; Depth = 0 })
  $guard = 0
  while ($stack.Count -gt 0 -and $guard -lt 700) {
    $guard++
    $item = $stack.Pop()
    try {
      $name = [string]$item.El.Current.Name
      $id = [string]$item.El.Current.AutomationId
      if ($name) { [void]$names.Add(("{0} [{1}]" -f $name, $id)) }
    } catch {}
    if ([int]$item.Depth -ge 8) { continue }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $null
    try { $child = $walker.GetFirstChild($item.El) } catch {}
    $kids = @(); $n = 0
    while ($null -ne $child -and $n -lt 50) {
      $kids += $child
      try { $child = $walker.GetNextSibling($child) } catch { break }
      $n++
    }
    for ($i = $kids.Count - 1; $i -ge 0; $i--) { $stack.Push(@{ El = $kids[$i]; Depth = ([int]$item.Depth + 1) }) }
  }
  return $names
}
$main = Get-Main
$cond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
  "MainWidget.mainBackgroundWidget.mainWidget.titleWidget.taskButton")
$btn = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
Write-Host "task button name=$($btn.Current.Name)"
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 1
$main = Get-Main
$names = Dump-Names $main
$names | Select-Object -Unique | ForEach-Object { Write-Host $_ }
$hwnd = [IntPtr]$main.Current.NativeWindowHandle
[void][TaskNative]::ShowWindow($hwnd, 9)
[void][TaskNative]::SetForegroundWindow($hwnd)
$r = New-Object TaskNative+RECT
[void][TaskNative]::GetWindowRect($hwnd, [ref]$r)
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [TaskNative]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$shot = Join-Path $PSScriptRoot "out\tasks.png"
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "print=$ok shot=$shot"
