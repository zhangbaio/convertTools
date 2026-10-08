$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class NativeWin2 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@

$root = [System.Windows.Automation.AutomationElement]::RootElement
$idCond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "MainWidget")
$main = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $idCond)
if ($null -eq $main) { throw "MainWidget not found" }
Write-Host "main=$($main.Current.Name) hwnd=$($main.Current.NativeWindowHandle)"

function Find-Id($el, $id) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Show-El($label, $el) {
  if ($null -eq $el) { Write-Host "$label MISSING"; return }
  $pats = @()
  foreach ($p in $el.GetSupportedPatterns()) { $pats += $p.ProgrammaticName }
  $toggle = ""
  $value = ""
  try {
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $toggle = " toggle=$($tp.Current.ToggleState)"
  } catch {}
  try {
    $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $value = " value='$($vp.Current.Value)'"
  } catch {}
  $r = $el.Current.BoundingRectangle
  Write-Host ("{0} name='{1}' enabled={2} offscreen={3} rect={4:N0},{5:N0} {6:N0}x{7:N0} patterns={8}{9}{10}" -f `
    $label, $el.Current.Name, $el.Current.IsEnabled, $el.Current.IsOffscreen, $r.X, $r.Y, $r.Width, $r.Height, ($pats -join ","), $toggle, $value)
}

$ids = @(
  "MainWidget.mainBackgroundWidget.mainWidget.titleWidget.openButton",
  "MainWidget.mainBackgroundWidget.mainWidget.titleWidget.saveButton",
  "MainWidget.mainBackgroundWidget.mainWidget.titleWidget.taskButton",
  "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.autoEraseWidget.switchButton",
  "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.watermarkEraseWidget.switchButton",
  "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.textEraseWidget.switchButton",
  "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.folderEdit",
  "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.okAndSaveButton",
  "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.closeButton",
  "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.customButton",
  "MainWidget.mainBackgroundWidget.mainWidget.mainStackedWidget.pagePlayer.PreviewMaskWidget"
)
foreach ($id in $ids) { Show-El $id (Find-Id $main $id) }

$mask = Find-Id $main "MainWidget.mainBackgroundWidget.mainWidget.mainStackedWidget.pagePlayer.PreviewMaskWidget"
if ($null -ne $mask) {
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  $child = $walker.GetFirstChild($mask)
  $i = 0
  while ($null -ne $child -and $i -lt 30) {
    Show-El "  mask-child" $child
    $grand = $walker.GetFirstChild($child)
    $j = 0
    while ($null -ne $grand -and $j -lt 20) {
      Show-El "    mask-grand" $grand
      $grand = $walker.GetNextSibling($grand)
      $j++
    }
    $child = $walker.GetNextSibling($child)
    $i++
  }
}

$hwnd = [IntPtr]$main.Current.NativeWindowHandle
[void][NativeWin2]::ShowWindow($hwnd, 9)
[void][NativeWin2]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 300
$rect = New-Object NativeWin2+RECT
[void][NativeWin2]::GetWindowRect($hwnd, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$shot = Join-Path $PSScriptRoot "out\video-window.png"
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "shot=$shot ${w}x${h}"
