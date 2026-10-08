$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WaitNative {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
$cfg = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "probe.json"), [Text.Encoding]::UTF8) | ConvertFrom-Json
$outputDir = $cfg.outputDir
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$logPath = Join-Path $PSScriptRoot "out\probe.log"
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
function Get-ValueText($el) {
  try { return [string]$el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { return "" }
}
function Set-ValueText($el, [string]$text) {
  $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
}
function Set-ToggleOn($el, [bool]$wantOn) {
  $pat = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  $on = $pat.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
  if ($on -ne $wantOn) { $pat.Toggle(); Start-Sleep -Milliseconds 300 }
}
function Get-AllNames($el) {
  $names = New-Object System.Collections.Generic.List[string]
  $stack = New-Object System.Collections.Generic.Stack[object]
  $stack.Push(@{ El = $el; Depth = 0 })
  $guard = 0
  while ($stack.Count -gt 0 -and $guard -lt 800) {
    $guard++
    $item = $stack.Pop()
    try {
      $name = [string]$item.El.Current.Name
      if ($name) { [void]$names.Add($name) }
    } catch {}
    if ([int]$item.Depth -ge 14) { continue }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $null
    try { $child = $walker.GetFirstChild($item.El) } catch {}
    $kids = @()
    $n = 0
    while ($null -ne $child -and $n -lt 60) {
      $kids += $child
      try { $child = $walker.GetNextSibling($child) } catch { break }
      $n++
    }
    for ($i = $kids.Count - 1; $i -ge 0; $i--) { $stack.Push(@{ El = $kids[$i]; Depth = ([int]$item.Depth + 1) }) }
  }
  return $names
}
function Save-Shot([string]$name) {
  $main = Get-Main
  $hwnd = [IntPtr]$main.Current.NativeWindowHandle
  $rect = New-Object WaitNative+RECT
  [void][WaitNative]::GetWindowRect($hwnd, [ref]$rect)
  $w = $rect.Right - $rect.Left
  $h = $rect.Bottom - $rect.Top
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
  $path = Join-Path $PSScriptRoot ("out\" + $name + ".png")
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Log "shot $path"
}

Log "wait for preview"
$ready = $false
$last = ""
$deadline = (Get-Date).AddMinutes(25)
$tick = 0
while ((Get-Date) -lt $deadline) {
  $names = @(Get-AllNames (Get-Main))
  $joined = ($names | Select-Object -Unique) -join " | "
  if ($joined -ne $last) { Log "ui: $joined"; $last = $joined }
  if ($joined -match "失败|不足|请登录|网络异常|无法完成") {
    Save-Shot "error"
    throw "meitu error: $joined"
  }
  $busy = $joined -match "AI消除中|请稍候|消除中"
  if (($joined -match "保存完整视频") -and -not $busy) { $ready = $true; break }
  $tick++
  if ($tick % 20 -eq 0) { Save-Shot "processing" }
  Start-Sleep -Seconds 3
}
if (-not $ready) { Save-Shot "preview-timeout"; throw "preview timeout" }
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
if ($null -eq $saveFull) { throw "save full button missing" }
Invoke-El $saveFull
$folderEl = $null
foreach ($i in 1..30) {
  Start-Sleep -Milliseconds 400
  $folderEl = Find-Id (Get-Main) "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.folderEdit"
  if ($null -ne $folderEl) { break }
}
if ($null -eq $folderEl) { throw "save dialog missing" }
$custom = Find-Id (Get-Main) "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.customButton"
Set-ToggleOn $custom $true
Set-ValueText $folderEl ($outputDir -replace "\\", "/")
Start-Sleep -Milliseconds 400
$actual = Get-ValueText (Find-Id (Get-Main) "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.folderEdit")
Log "save path=$actual"
if ($actual -notmatch "去字幕") { throw "save path was not applied: $actual" }
Save-Shot "save-dialog"
Invoke-El (Find-Id (Get-Main) "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.okAndSaveButton")
Log "clicked save"

$stable = $null
$lastSize = -1
$same = 0
$deadline = (Get-Date).AddMinutes(8)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 2
  $candidate = Get-ChildItem -LiteralPath $outputDir -File -Filter *.mp4 -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
  if ($null -eq $candidate) { continue }
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
