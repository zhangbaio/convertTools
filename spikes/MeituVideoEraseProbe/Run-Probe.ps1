$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ProbeNative {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
  public static void ForceForeground(IntPtr hwnd) {
    uint unused;
    uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out unused);
    uint thisThread = GetCurrentThreadId();
    uint targetThread = GetWindowThreadProcessId(hwnd, out unused);
    AttachThreadInput(thisThread, fgThread, true);
    AttachThreadInput(thisThread, targetThread, true);
    ShowWindow(hwnd, 9);
    SetForegroundWindow(hwnd);
    AttachThreadInput(thisThread, targetThread, false);
    AttachThreadInput(thisThread, fgThread, false);
  }
}
"@

$outDirScript = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force -Path $outDirScript | Out-Null
$logPath = Join-Path $outDirScript "probe.log"
$cfg = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "probe.json"), [Text.Encoding]::UTF8) | ConvertFrom-Json
$inputPath = $cfg.input
$outputDir = $cfg.outputDir
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
if (-not (Test-Path -LiteralPath $inputPath)) { throw "input missing: $inputPath" }

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
  if ($null -eq $el) { throw "video erase window MainWidget not found" }
  return $el
}

function Find-Id($el, [string]$id) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-El($el) {
  $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
  $pat.Invoke()
}

function Set-ToggleOn($el, [bool]$wantOn) {
  $pat = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  $on = $pat.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
  if ($on -ne $wantOn) {
    $pat.Toggle()
    Start-Sleep -Milliseconds 400
  }
  $pat2 = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  return ($pat2.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
}

function Get-ValueText($el) {
  try {
    $pat = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    return [string]$pat.Current.Value
  } catch { return "" }
}

function Set-ValueText($el, [string]$text) {
  $pat = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $pat.SetValue($text)
}

$script:shotIndex = 0
function Save-Shot([string]$name) {
  $main = Get-Main
  $hwnd = [IntPtr]$main.Current.NativeWindowHandle
  $rect = New-Object ProbeNative+RECT
  [void][ProbeNative]::GetWindowRect($hwnd, [ref]$rect)
  $w = $rect.Right - $rect.Left
  $h = $rect.Bottom - $rect.Top
  if ($w -lt 50 -or $h -lt 50) { return }
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
    $depth = $item.Depth
    $name = ""
    try { $name = [string]$node.Current.Name } catch {}
    if ($name) { $names.Add($name) | Out-Null }
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
    for ($i = $kids.Count - 1; $i -ge 0; $i--) {
      $stack.Push(@{ El = $kids[$i]; Depth = $depth + 1 })
    }
  }
  return $names
}

function Get-TopWindows {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  $child = $walker.GetFirstChild($root)
  $list = @()
  $n = 0
  while ($null -ne $child -and $n -lt 40) {
    $title = ""
    $className = ""
    try { $title = [string]$child.Current.Name } catch {}
    try { $className = [string]$child.Current.ClassName } catch {}
    if ($title -or $className) {
      $list += [pscustomobject]@{ El = $child; Title = $title; Class = $className }
    }
    $child = $walker.GetNextSibling($child)
    $n++
  }
  return $list
}

function Focus-Main {
  $main = Get-Main
  [ProbeNative]::ForceForeground([IntPtr]$main.Current.NativeWindowHandle)
}

Log "probe start input=$inputPath output=$outputDir"
Focus-Main
Save-Shot "before-close"

$main = Get-Main
$close = Find-Id $main "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.closeButton"
if ($null -ne $close) {
  $folder = Find-Id $main "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.folderEdit"
  Log "closing leftover save dialog path=$(Get-ValueText $folder)"
  Invoke-El $close
  $gone = $false
  foreach ($i in 1..20) {
    Start-Sleep -Milliseconds 300
    $main = Get-Main
    if ($null -eq (Find-Id $main "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.okAndSaveButton")) {
      $gone = $true
      break
    }
  }
  if (-not $gone) { throw "save dialog did not close" }
  Log "leftover save dialog closed"
}
Save-Shot "after-close"

Focus-Main
$main = Get-Main
$openBtn = Find-Id $main "MainWidget.mainBackgroundWidget.mainWidget.titleWidget.openButton"
if ($null -eq $openBtn) { throw "open button missing" }
Log "click 打开新视频"
Invoke-El $openBtn

$dialog = $null
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 400
  $wins = Get-TopWindows
  foreach ($w in $wins) {
    if ($w.Title -match "打开|选择|浏览|Open|视频文件") {
      $dialog = $w
      break
    }
  }
  if ($null -ne $dialog) { break }
  $main = Get-Main
  $names = Get-AllNames $main
  $joined = $names -join " | "
  if ($joined -match "不保存|放弃|尚未保存|替换") {
    Log "in-app confirm: $joined"
    Save-Shot "confirm"
    throw "unexpected confirm dialog: $joined"
  }
}
if ($null -eq $dialog) {
  $titles = (Get-TopWindows | ForEach-Object { $_.Title }) -join " || "
  Save-Shot "no-dialog"
  throw "file dialog not found. windows=$titles"
}
Log "file dialog title=$($dialog.Title) class=$($dialog.Class)"

function Find-OpenEdit($dlg) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Edit)
  $edits = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  $best = $null
  foreach ($ed in $edits) {
    $id = ""
    try { $id = [string]$ed.Current.AutomationId } catch {}
    $name = ""
    try { $name = [string]$ed.Current.Name } catch {}
    Log "dialog edit id=$id name=$name value=$(Get-ValueText $ed)"
    if ($id -eq "1148" -or $id -eq "FileNameControlHost" -or $name -match "文件名|File name") { return $ed }
    $best = $ed
  }
  return $best
}

$edit = Find-OpenEdit $dialog.El
if ($null -eq $edit) { throw "filename edit not found" }
Set-ValueText $edit $inputPath
Start-Sleep -Milliseconds 300
Log "filename now=$(Get-ValueText $edit)"

$btnCond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
  [System.Windows.Automation.ControlType]::Button)
$buttons = $dialog.El.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
$openClick = $null
foreach ($b in $buttons) {
  $name = ""
  try { $name = [string]$b.Current.Name } catch {}
  Log "dialog button=$name"
  if ($name -match "^打开|^Open") { $openClick = $b }
}
if ($null -eq $openClick) { throw "open confirm button not found" }
Invoke-El $openClick
Log "clicked file dialog open"

$loaded = $false
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 1
  $json = [IO.File]::ReadAllText("C:\Users\PC\AppData\Local\Meitu\MTXXVideo\Config.json", [Text.Encoding]::UTF8)
  if ($json -match "1133760793") {
    $loaded = $true
    break
  }
  $wins = Get-TopWindows | Where-Object { $_.Title -and $_.Title -notmatch "美图秀秀" }
  if ($wins) { Log ("extra windows: " + (($wins | ForEach-Object { $_.Title }) -join " || ")) }
}
if (-not $loaded) {
  Save-Shot "open-timeout"
  throw "video was not recorded as last open file"
}
Log "config last open contains target video"
Save-Shot "opened"

function Wait-Names([scriptblock]$pred, [int]$seconds, [string]$shotName) {
  $deadline = (Get-Date).AddSeconds($seconds)
  $last = ""
  while ((Get-Date) -lt $deadline) {
    $main = Get-Main
    $names = @(Get-AllNames $main)
    $joined = ($names | Select-Object -Unique) -join " | "
    if ($joined -ne $last) {
      Log "ui: $joined"
      $last = $joined
    }
    if (& $pred $names) { return $names }
    if ($joined -match "失败|不足|请登录|网络异常|无法") {
      Save-Shot "error"
      throw "meitu reported an error: $joined"
    }
    Start-Sleep -Seconds 2
  }
  Save-Shot $shotName
  throw "timeout waiting: $shotName"
}

$main = Get-Main
$auto = Find-Id $main "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.autoEraseWidget.switchButton"
$wm = Find-Id $main "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.watermarkEraseWidget.switchButton"
$tx = Find-Id $main "MainWidget.mainBackgroundWidget.mainWidget.parameterWidget.textEraseWidget.switchButton"
if ($null -eq $auto) { throw "smart erase switch missing" }
$wmOn = Set-ToggleOn $wm $false
$txOn = Set-ToggleOn $tx $false
Log "watermarkOn=$wmOn subtitleOn=$txOn"
$namesNow = @(Get-AllNames (Get-Main))
$alreadyRunning = ($namesNow -join " ") -match "AI消除中|请稍候"
if (-not $alreadyRunning) {
  $autoOn = Set-ToggleOn $auto $true
  Log "smartEraseOn=$autoOn"
  if (-not $autoOn) { throw "failed to turn on 智能全消" }
} else {
  Log "processing already visible, leave switch"
}
Save-Shot "switch-on"

Log "waiting for preview to finish"
Wait-Names {
  param($names)
  $text = $names -join " "
  $busy = $text -match "AI消除中|请稍候|消除中"
  $ready = $text -match "保存完整视频"
  return ((-not $busy) -and $ready)
} 1200 "preview-timeout"
Save-Shot "preview-ready"
Log "preview ready"

$main = Get-Main
$saveFull = $null
$btnCond2 = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
  [System.Windows.Automation.ControlType]::Button)
foreach ($b in $main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond2)) {
  $name = ""
  try { $name = [string]$b.Current.Name } catch {}
  if ($name -eq "保存完整视频") { $saveFull = $b }
}
if ($null -eq $saveFull) { throw "保存完整视频 button missing" }
Log "click 保存完整视频"
Invoke-El $saveFull

$folderEl = $null
$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 400
  $main = Get-Main
  $folderEl = Find-Id $main "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.folderEdit"
  if ($null -ne $folderEl) { break }
}
if ($null -eq $folderEl) { throw "save settings dialog did not open" }
$custom = Find-Id $main "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.customButton"
[void](Set-ToggleOn $custom $true)
$folderValue = $outputDir -replace "\\", "/"
Set-ValueText $folderEl $folderValue
Start-Sleep -Milliseconds 300
Log "save path now=$(Get-ValueText $folderEl)"
Save-Shot "save-dialog"
$ok = Find-Id (Get-Main) "MainWidget.MaskDialog.MaskCenterWidget.ExportSettingsMaskWidget.okAndSaveButton"
if ($null -eq $ok) { throw "立即保存 missing" }
$before = @(Get-ChildItem -LiteralPath $outputDir -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
Log "click 立即保存"
Invoke-El $ok

Log "waiting for exported mp4"
$stable = $null
$lastSize = -1
$same = 0
$deadline = (Get-Date).AddSeconds(300)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 2
  $files = @(Get-ChildItem -LiteralPath $outputDir -File -Filter *.mp4 -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)
  $fresh = $files | Where-Object { $before -notcontains $_.FullName -or $_.LastWriteTime -gt (Get-Date).AddMinutes(-10) }
  $candidate = $files | Select-Object -Last 1
  if ($null -eq $candidate) { continue }
  Log "candidate=$($candidate.Name) size=$($candidate.Length)"
  if ($candidate.Length -gt 1MB -and $candidate.Length -eq $lastSize) {
    $same++
    if ($same -ge 3) { $stable = $candidate; break }
  } else {
    $same = 0
    $lastSize = $candidate.Length
  }
}
if ($null -eq $stable) {
  Save-Shot "export-timeout"
  throw "export file did not stabilize"
}
Save-Shot "saved"
Log "DONE $($stable.FullName) size=$($stable.Length)"
Write-Host "DONE $($stable.FullName)"
