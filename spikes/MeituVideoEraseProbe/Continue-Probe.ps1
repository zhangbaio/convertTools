$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ContNative {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
  public const int WM_SETTEXT = 0x000C;
  public const int BM_CLICK = 0x00F5;
}
"@

$cfg = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "probe.json"), [Text.Encoding]::UTF8) | ConvertFrom-Json
$inputPath = $cfg.input
$outputDir = $cfg.outputDir
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$outDirScript = Join-Path $PSScriptRoot "out"
$logPath = Join-Path $outDirScript "probe.log"

function Log([string]$msg) {
  $line = "{0:HH:mm:ss} {1}" -f (Get-Date), $msg
  Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
  Write-Host $line
}

function Get-Text($h) {
  $sb = New-Object System.Text.StringBuilder 512
  [void][ContNative]::GetWindowText($h, $sb, $sb.Capacity)
  return $sb.ToString()
}
function Get-Cls($h) {
  $sb = New-Object System.Text.StringBuilder 256
  [void][ContNative]::GetClassName($h, $sb, $sb.Capacity)
  return $sb.ToString()
}

function Find-OpenDialog {
  $script:found = [IntPtr]::Zero
  $proc = Get-Process MTXXVideo -ErrorAction SilentlyContinue
  if ($null -eq $proc) { return [IntPtr]::Zero }
  $want = $proc.Id
  $enum = [ContNative+EnumProc]{
    param($h, $l)
    $procId = [uint32]0
    [void][ContNative]::GetWindowThreadProcessId($h, [ref]$procId)
    if ([int]$procId -eq $want -and [ContNative]::IsWindowVisible($h) -and (Get-Cls $h) -eq "#32770" -and (Get-Text $h) -eq "打开视频") {
      $script:found = $h
    }
    return $true
  }
  [void][ContNative]::EnumWindows($enum, [IntPtr]::Zero)
  return $script:found
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
function Set-ToggleOn($el, [bool]$wantOn) {
  $pat = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  $on = $pat.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
  if ($on -ne $wantOn) { $pat.Toggle(); Start-Sleep -Milliseconds 500 }
  $pat = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  return ($pat.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
}
function Get-ValueText($el) {
  try { return [string]$el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { return "" }
}
function Set-ValueText($el, [string]$text) {
  $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
}

$script:shotIndex = 10
function Save-Shot([string]$name) {
  $main = Get-Main
  $hwnd = [IntPtr]$main.Current.NativeWindowHandle
  $rect = New-Object ContNative+RECT
  [void][ContNative]::GetWindowRect($hwnd, [ref]$rect)
  $w = [Math]::Max($rect.Right - $rect.Left, 1)
  $h = [Math]::Max($rect.Bottom - $rect.Top, 1)
  $left = [Math]::Min($rect.Left, 0)
  # capture a wider area so the file dialog above the window is included
  $capLeft = $rect.Left - 40
  $capTop = $rect.Top - 80
  $capW = $w + 80
  $capH = $h + 160
  $bmp = New-Object System.Drawing.Bitmap $capW, $capH
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($capLeft, $capTop, 0, 0, (New-Object System.Drawing.Size($capW, $capH)))
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
      if ($name) { $names.Add($name) | Out-Null }
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

Log "continue probe"
$dlg = Find-OpenDialog
if ($dlg -eq [IntPtr]::Zero) {
  Log "dialog missing, click open again"
  $main = Get-Main
  $openBtn = Find-Id $main "MainWidget.mainBackgroundWidget.mainWidget.titleWidget.openButton"
  Invoke-El $openBtn
  foreach ($i in 1..20) {
    Start-Sleep -Milliseconds 400
    $dlg = Find-OpenDialog
    if ($dlg -ne [IntPtr]::Zero) { break }
  }
}
if ($dlg -eq [IntPtr]::Zero) { throw "打开视频 dialog not found" }
Log "dialog hwnd=$($dlg.ToInt64())"

$script:edit = [IntPtr]::Zero
$script:openBtnHwnd = [IntPtr]::Zero
$childEnum = [ContNative+EnumProc]{
  param($h, $l)
  $cls = Get-Cls $h
  $title = Get-Text $h
  if ($cls -eq "Edit" -and [ContNative]::IsWindowVisible($h)) { $script:edit = $h }
  if ($cls -eq "Button" -and $title -like "打开*") { $script:openBtnHwnd = $h }
  return $true
}
[void][ContNative]::EnumChildWindows($dlg, $childEnum, [IntPtr]::Zero)
if ($script:edit -eq [IntPtr]::Zero) { throw "filename edit missing" }
if ($script:openBtnHwnd -eq [IntPtr]::Zero) { throw "open button missing" }
Log "edit=$($script:edit.ToInt64()) button=$($script:openBtnHwnd.ToInt64())"
[void][ContNative]::SetForegroundWindow($dlg)
[void][ContNative]::SendMessage($script:openBtnHwnd, [ContNative]::BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero)
Log "clicked 打开"
Start-Sleep -Seconds 1
if ([ContNative]::IsWindow($dlg) -and [ContNative]::IsWindowVisible($dlg)) {
  Save-Shot "dialog-still-open"
  Log "dialog still open after click"
} else {
  Log "dialog closed"
}

$loaded = $false
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 1
  $json = [IO.File]::ReadAllText("C:\Users\PC\AppData\Local\Meitu\MTXXVideo\Config.json", [Text.Encoding]::UTF8)
  if ($json -match "1133760793") { $loaded = $true; break }
  if ([ContNative]::IsWindow($dlg) -and [ContNative]::IsWindowVisible($dlg)) {
    Log "still waiting, dialog remains, box=$(Get-Text $script:edit)"
  }
}
if (-not $loaded) {
  Save-Shot "open-timeout"
  throw "target video was not opened"
}
Log "target video opened"
Save-Shot "opened"

function Wait-Names([scriptblock]$pred, [int]$seconds, [string]$shotName) {
  $deadline = (Get-Date).AddSeconds($seconds)
  $last = ""
  while ((Get-Date) -lt $deadline) {
    $names = @(Get-AllNames (Get-Main))
    $joined = ($names | Select-Object -Unique) -join " | "
    if ($joined -ne $last) { Log "ui: $joined"; $last = $joined }
    if ($joined -match "失败|不足|请登录|网络异常|无法完成") {
      Save-Shot "error"
      throw "meitu error: $joined"
    }
    if (& $pred $names) { return $names }
    Start-Sleep -Seconds 2
  }
  Save-Shot $shotName
  throw "timeout $shotName"
}

$main = Get-Main
$auto = Find-Id $main "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.autoEraseWidget.switchButton"
$wm = Find-Id $main "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.watermarkEraseWidget.switchButton"
$tx = Find-Id $main "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.textEraseWidget.switchButton"
[void](Set-ToggleOn $wm $false)
[void](Set-ToggleOn $tx $false)
$namesNow = @(Get-AllNames (Get-Main))
$running = ($namesNow -join " ") -match "AI消除中|请稍候"
if (-not $running) {
  $on = Set-ToggleOn $auto $true
  Log "smartEraseOn=$on"
  if (-not $on) { throw "could not enable smart erase" }
  Start-Sleep -Seconds 2
  $namesNow = @(Get-AllNames (Get-Main))
  $running = ($namesNow -join " ") -match "AI消除中|请稍候"
  if (-not $running) {
    Log "progress not visible, toggle cycle"
    [void](Set-ToggleOn $auto $false)
    Start-Sleep -Milliseconds 600
    $on = Set-ToggleOn $auto $true
    Log "smartEraseOnAfterCycle=$on"
  }
} else {
  Log "progress already visible"
}
Save-Shot "switch-on"

Log "waiting until progress has started"
$seenBusy = $false
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
  $names = @(Get-AllNames (Get-Main))
  $text = $names -join " "
  if ($text -match "AI消除中|请稍候|消除中") { $seenBusy = $true; Log "progress started"; break }
  Start-Sleep -Seconds 2
}
if (-not $seenBusy) {
  Save-Shot "no-progress"
  Log "ui at no-progress: $((@(Get-AllNames (Get-Main)) | Select-Object -Unique) -join ' | ')"
  throw "smart erase did not start"
}

Log "waiting for full preview"
Wait-Names {
  param($names)
  $text = $names -join " "
  return (($text -notmatch "AI消除中|请稍候") -and ($text -match "保存完整视频"))
} 1500 "preview-timeout"
Save-Shot "preview-ready"
Log "preview ready, opening save dialog"

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
$folderValue = ($outputDir -replace "\\", "/")
Set-ValueText $folderEl $folderValue
Start-Sleep -Milliseconds 300
$actual = Get-ValueText (Find-Id (Get-Main) "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.folderEdit")
Log "save path=$actual"
if ($actual -notmatch "去字幕") { throw "save path was not applied: $actual" }
Save-Shot "save-dialog"
$before = @(Get-ChildItem -LiteralPath $outputDir -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
Log "existing files=$($before -join ',')"
Invoke-El (Find-Id (Get-Main) "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.okAndSaveButton")
Log "clicked 立即保存"

$stable = $null
$lastSize = -1
$same = 0
$deadline = (Get-Date).AddSeconds(420)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 2
  $candidate = Get-ChildItem -LiteralPath $outputDir -File -Filter *.mp4 -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
  if ($null -eq $candidate) {
    $names = @(Get-AllNames (Get-Main))
    $joined = ($names | Select-Object -Unique) -join " | "
    if ($joined -match "失败|不足|无法") { Save-Shot "export-error"; throw "export error: $joined" }
    continue
  }
  if ($before -contains $candidate.Name -and $candidate.LastWriteTime -lt (Get-Date).AddMinutes(-15)) { continue }
  Log "candidate=$($candidate.FullName) size=$($candidate.Length)"
  if ($candidate.Length -gt 1MB -and $candidate.Length -eq $lastSize) {
    $same++
    if ($same -ge 3) { $stable = $candidate; break }
  } else { $same = 0; $lastSize = $candidate.Length }
}
if ($null -eq $stable) { Save-Shot "export-timeout"; throw "export did not finish" }
Save-Shot "saved"
Log "DONE $($stable.FullName) size=$($stable.Length)"
Write-Host "DONE $($stable.FullName)"
