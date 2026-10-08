$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class InvNative {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
[void][InvNative]::SetProcessDPIAware()
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "MainWidget")
$main = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$id = "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.autoEraseWidget.switchButton"
$c = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
$el = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
$pats = @()
foreach ($p in $el.GetSupportedPatterns()) { $pats += $p.ProgrammaticName }
Write-Host ("patterns=" + ($pats -join ","))
$toggle = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
Write-Host "toggle=$($toggle.Current.ToggleState)"
$hwnd = [IntPtr]$main.Current.NativeWindowHandle
[void][InvNative]::SetForegroundWindow($hwnd)
try {
  $acc = $el.GetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern)
  Write-Host "default=$($acc.Current.DefaultAction) state=$($acc.Current.State)"
  $acc.DoDefaultAction()
  Write-Host "did default action"
} catch {
  Write-Host "no legacy: $($_.Exception.Message)"
  $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Write-Host "did invoke"
}
Start-Sleep -Milliseconds 700
$toggle = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
Write-Host "toggle-after=$($toggle.Current.ToggleState)"
$rect = New-Object InvNative+RECT
[void][InvNative]::GetWindowRect($hwnd, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$shot = Join-Path $PSScriptRoot "out\invoke-switch.png"
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "shot=$shot"
