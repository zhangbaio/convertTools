$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class CheckNative {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "MainWidget")
$main = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($null -eq $main) { Write-Host "NO MAIN"; exit 1 }
$ids = @(
  "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.okAndSaveButton",
  "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.folderEdit",
  "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.closeButton"
)
foreach ($id in $ids) {
  $c = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  $el = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
  if ($null -eq $el) { Write-Host "MISSING $id"; continue }
  $r = $el.Current.BoundingRectangle
  $val = ""
  try { $val = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
  Write-Host ("FOUND {0} name={1} rect={2:N0},{3:N0} {4:N0}x{5:N0} value={6}" -f $id, $el.Current.Name, $r.X, $r.Y, $r.Width, $r.Height, $val)
}
$hwnd = [IntPtr]$main.Current.NativeWindowHandle
$rect = New-Object CheckNative+RECT
[void][CheckNative]::GetWindowRect($hwnd, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$shot = Join-Path $PSScriptRoot "out\save-now.png"
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "shot=$shot"
