$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class NativeWin {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@

$outDir = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$names = @("XiuXiu", "MTXXVideo", "MTXXPhotoViewer")
$procs = Get-Process | Where-Object { $names -contains $_.ProcessName }
$pids = @($procs | ForEach-Object { $_.Id })
Write-Host ("pids=" + ($pids -join ","))

$script:wins = New-Object System.Collections.Generic.List[object]
$enum = [NativeWin+EnumProc]{
  param($h, $l)
  $procId = [uint32]0
  [void][NativeWin]::GetWindowThreadProcessId($h, [ref]$procId)
  if ($pids -contains [int]$procId -and [NativeWin]::IsWindowVisible($h)) {
    $title = New-Object System.Text.StringBuilder 512
    $cls = New-Object System.Text.StringBuilder 256
    [void][NativeWin]::GetWindowText($h, $title, $title.Capacity)
    [void][NativeWin]::GetClassName($h, $cls, $cls.Capacity)
    $rect = New-Object NativeWin+RECT
    [void][NativeWin]::GetWindowRect($h, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $hgt = $rect.Bottom - $rect.Top
    if ($w -gt 80 -and $hgt -gt 80) {
      $script:wins.Add([pscustomobject]@{
        Pid = [int]$procId
        Hwnd = $h.ToInt64()
        Title = $title.ToString()
        Class = $cls.ToString()
        Left = $rect.Left
        Top = $rect.Top
        Width = $w
        Height = $hgt
      }) | Out-Null
    }
  }
  return $true
}
[void][NativeWin]::EnumWindows($enum, [IntPtr]::Zero)

$winFile = Join-Path $outDir "windows.json"
$script:wins | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $winFile -Encoding UTF8
$script:wins | Format-Table -AutoSize | Out-String -Width 220 | Write-Host

function Dump-Element($el, $depth, $sb) {
  if ($null -eq $el -or $depth -gt 6) { return }
  $name = ""
  $ctype = ""
  $aid = ""
  $className = ""
  try { $name = $el.Current.Name } catch {}
  try { $ctype = $el.Current.ControlType.ProgrammaticName } catch {}
  try { $aid = $el.Current.AutomationId } catch {}
  try { $className = $el.Current.ClassName } catch {}
  $indent = "  " * $depth
  [void]$sb.AppendLine("$indent[$ctype] id='$aid' class='$className' name='$name'")
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  $child = $walker.GetFirstChild($el)
  $count = 0
  while ($null -ne $child -and $count -lt 80) {
    Dump-Element $child ($depth + 1) $sb
    $child = $walker.GetNextSibling($child)
    $count++
  }
}

$treeFile = Join-Path $outDir "uia-tree.txt"
$sb = New-Object System.Text.StringBuilder
foreach ($w in $script:wins) {
  [void]$sb.AppendLine("===== hwnd=$($w.Hwnd) pid=$($w.Pid) title=$($w.Title) class=$($w.Class) =====")
  try {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$w.Hwnd)
    Dump-Element $el 0 $sb
  } catch {
    [void]$sb.AppendLine("UIA error: $($_.Exception.Message)")
  }
  [void]$sb.AppendLine("")
}
[IO.File]::WriteAllText($treeFile, $sb.ToString(), [Text.UTF8Encoding]::new($false))
Write-Host "tree=$treeFile bytes=$((Get-Item $treeFile).Length)"

$video = $script:wins | Where-Object { $_.Title -like "*视频*" } | Select-Object -First 1
if ($null -eq $video) { $video = $script:wins | Sort-Object Width -Descending | Select-Object -First 1 }
if ($null -ne $video) {
  $hwnd = [IntPtr]$video.Hwnd
  [void][NativeWin]::ShowWindow($hwnd, 9)
  [void][NativeWin]::SetForegroundWindow($hwnd)
  Start-Sleep -Milliseconds 400
  $rect = New-Object NativeWin+RECT
  [void][NativeWin]::GetWindowRect($hwnd, [ref]$rect)
  $w = $rect.Right - $rect.Left
  $hgt = $rect.Bottom - $rect.Top
  $bmp = New-Object System.Drawing.Bitmap $w, $hgt
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $hgt)))
  $shot = Join-Path $outDir "window.png"
  $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose()
  $bmp.Dispose()
  Write-Host "shot=$shot ${w}x${hgt}"
}
