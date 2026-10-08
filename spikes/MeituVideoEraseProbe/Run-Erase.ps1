$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class EraseNative {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, System.UIntPtr extra);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
  public const uint LEFTDOWN = 0x0002;
  public const uint LEFTUP = 0x0004;
}
"@

[void][EraseNative]::SetProcessDPIAware()
$cfg = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "probe.json"), [Text.Encoding]::UTF8) | ConvertFrom-Json
$outputDir = $cfg.outputDir
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$outDirScript = Join-Path $PSScriptRoot "out"
$logPath = Join-Path $outDirScript "probe.log"
function Log([string]$msg) {
  $line = "{0:HH:mm:ss} {1}" -f (Get-Date), $msg
  Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
  Write-Host $line
}
function Get-Main {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "MainWidget")
  $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if ($null -eq $el) { throw "MainWidget missing" }
  return $el
}
function Find-Id($el, [string]$id) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Click-Center($el) {
  $r = $el.Current.BoundingRectangle
  $x = [int]($r.X + $r.Width / 2)
  $y = [int]($r.Y + $r.Height / 2)
  $hwnd = [IntPtr](Get-Main).Current.NativeWindowHandle
  [void][EraseNative]::ShowWindow($hwnd, 9)
  [void][EraseNative]::SetForegroundWindow($hwnd)
  Start-Sleep -Milliseconds 300
  Log "click $x,$y size=$($r.Width)x$($r.Height)"
  [void][EraseNative]::SetCursorPos($x, $y)
  Start-Sleep -Milliseconds 80
  [EraseNative]::mouse_event([EraseNative]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 60
  [EraseNative]::mouse_event([EraseNative]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
}
function Set-ToggleOn($el, [bool]$wantOn) {
  $pat = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  $on = $pat.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
  Log "toggle before=$on want=$wantOn id=$($el.Current.AutomationId)"
  if ($on -ne $wantOn) { $pat.Toggle(); Start-Sleep -Milliseconds 600 }
  $pat = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  $now = $pat.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
  Log "toggle after=$now"
  return $now
}
function Get-ValueText($el) {
  try { return [string]$el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { return "" }
}
function Set-ValueText($el, [string]$text) {
  $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
}
$script:shotIndex = 20
function Save-Shot([string]$name) {
  $main = Get-Main
  $hwnd = [IntPtr]$main.Current.NativeWindowHandle
  [void][EraseNative]::ShowWindow($hwnd, 9)
  [void][EraseNative]::SetForegroundWindow($hwnd)
  $rect = New-Object EraseNative+RECT
  [void][EraseNative]::GetWindowRect($hwnd, [ref]$rect)
  $w = $rect.Right - $rect.Left
  $h = $rect.Bottom - $rect.Top
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
  $script:shotIndex++
  $path = Join-Path $outDirScript ("{0:D2}-{1}.png" -f $script:shotIndex, $name)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Log "shot $path"
}
function Get-AllNames($el) {
  $names = New-Object System.Collections.Generic.List[string]
  $stack = New-Object System.Collections.Generic.Stack[object]
  $stack.Push(@{ El = $el; Depth = 0 })
  $guard = 0
  while ($stack.Count -gt 0 -and $guard -lt 800) {
    $guard++
    $item = $stack.Pop()
    $node = $item.El
    $depth = [int]$item.Depth
    try {
      $name = [string]$node.Current.Name
      if ($name) { [void]$names.Add($name) }
    } catch {}
    if ($depth -ge 14) { continue }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $null
    try { $child = $walker.GetFirstChild($node) } catch {}
    $kids = @()
    $n = 0
    while ($null -ne $child -and $n -lt 60) {
      $kids += $child
      try { $child = $walker.GetNextSibling($child) } catch { break }
      $n++
    }
    for ($i = $kids.Count - 1; $i -ge 0; $i--) { $stack.Push(@{ El = $kids[$i]; Depth = ($depth + 1) }) }
  }
  return $names
}
function Wait-Names([scriptblock]$pred, [int]$seconds, [string]$shotName) {
  $deadline = (Get-Date).AddSeconds($seconds)
  $last = ""
  $tick = 0
  while ((Get-Date) -lt $deadline) {
    $names = @(Get-AllNames (Get-Main))
    $joined = ($names | Select-Object -Unique) -join " | "
    if ($joined -ne $last) { Log "ui: $joined"; $last = $joined }
    if ($joined -match "失败|不足|请登录|网络异常|无法完成") {
      Save-Shot "error"
      throw "meitu error: $joined"
    }
    if (& $pred $names) { return $names }
    $tick++
    if ($tick % 15 -eq 0) { Save-Shot "wait-$tick" }
    Start-Sleep -Seconds 2
  }
  Save-Shot $shotName
  throw "timeout $shotName"
}

Log "erase phase start output=$outputDir"
Save-Shot "before-switch"
$main = Get-Main
$auto = Find-Id $main "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.autoEraseWidget.switchButton"
$wm = Find-Id $main "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.watermarkEraseWidget.switchButton"
$tx = Find-Id $main "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.textEraseWidget.switchButton"
if ($null -eq $auto) { throw "smart erase switch missing" }
Click-Center $auto
Start-Sleep -Milliseconds 800
Save-Shot "switch-clicked"

$seenBusy = $false
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline) {
  $text = @(Get-AllNames (Get-Main)) -join " "
  if ($text -match "AI消除中|请稍候|消除中") { $seenBusy = $true; break }
  Start-Sleep -Seconds 1
}
if (-not $seenBusy) {
  Log "progress not visible, click switch again"
  Click-Center (Find-Id (Get-Main) "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.autoEraseWidget.switchButton")
  Save-Shot "clicked-again"
  $deadline = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $deadline) {
    $text = @(Get-AllNames (Get-Main)) -join " "
    if ($text -match "AI消除中|请稍候|消除中") { $seenBusy = $true; break }
    Start-Sleep -Seconds 1
  }
}
if (-not $seenBusy) {
  Save-Shot "no-progress"
  Log "ui: $((@(Get-AllNames (Get-Main)) | Select-Object -Unique) -join ' | ')"
  throw "smart erase did not start"
}
Log "progress started"
Log "waiting for preview"
Wait-Names {
  param($names)
  $text = $names -join " "
  return (($text -notmatch "AI消除中|请稍候") -and ($text -match "保存完整视频"))
} 1500 "preview-timeout"
Save-Shot "preview-ready"
Log "preview ready"

$main = Get-Main
$saveFull = $null
$btnCond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
  [System.Windows.Automation.ControlType]::Button)
foreach ($b in $main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
  try { if ([string]$b.Current.Name -eq "保存完整视频") { $saveFull = $b } } catch {}
}
if ($null -eq $saveFull) { throw "保存完整视频 missing" }
Invoke-El $saveFull
$folderEl = $null
foreach ($i in 1..30) {
  Start-Sleep -Milliseconds 400
  $folderEl = Find-Id (Get-Main) "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.folderEdit"
  if ($null -ne $folderEl) { break }
}
if ($null -eq $folderEl) { throw "save dialog missing" }
$custom = Find-Id (Get-Main) "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.customButton"
[void](Set-ToggleOn $custom $true)
Set-ValueText $folderEl ($outputDir -replace "\\", "/")
Start-Sleep -Milliseconds 400
$actual = Get-ValueText (Find-Id (Get-Main) "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.folderEdit")
Log "save path=$actual"
if ($actual -notmatch "奇妙动物园|去字幕") { throw "save path was not applied: $actual" }
Save-Shot "save-dialog"
$before = @(Get-ChildItem -LiteralPath $outputDir -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
Invoke-El (Find-Id (Get-Main) "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.okAndSaveButton")
Log "clicked save"

$stable = $null
$lastSize = -1
$same = 0
$deadline = (Get-Date).AddSeconds(420)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 2
  $candidate = Get-ChildItem -LiteralPath $outputDir -File -Filter *.mp4 -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
  if ($null -eq $candidate) { continue }
  if ($before -contains $candidate.Name -and $candidate.LastWriteTime -lt (Get-Date).AddMinutes(-20)) { continue }
  Log "candidate=$($candidate.Name) size=$($candidate.Length)"
  if ($candidate.Length -gt 1MB -and $candidate.Length -eq $lastSize) {
    $same++
    if ($same -ge 3) { $stable = $candidate; break }
  } else { $same = 0; $lastSize = $candidate.Length }
}
if ($null -eq $stable) { Save-Shot "export-timeout"; throw "export did not finish" }
Save-Shot "saved"
Log "DONE $($stable.FullName) size=$($stable.Length)"
Write-Host "DONE $($stable.FullName)"
