$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SetTextNative {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr hDlg, int nID);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")]
  public static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
  public const int WM_SETTEXT = 0x000C;
}
"@
Add-Type -AssemblyName System.Drawing
$cfg = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "probe.json"), [Text.Encoding]::UTF8) | ConvertFrom-Json
$path = $cfg.input
$proc = Get-Process MTXXVideo
$script:dlg = [IntPtr]::Zero
$enum = [SetTextNative+EnumProc]{
  param($h, $l)
  $procId = [uint32]0
  [void][SetTextNative]::GetWindowThreadProcessId($h, [ref]$procId)
  if ([int]$procId -ne $proc.Id -or -not [SetTextNative]::IsWindowVisible($h)) { return $true }
  $cls = New-Object System.Text.StringBuilder 128
  $title = New-Object System.Text.StringBuilder 128
  [void][SetTextNative]::GetClassName($h, $cls, 128)
  [void][SetTextNative]::GetWindowText($h, $title, 128)
  if ($cls.ToString() -eq "#32770" -and $title.ToString() -eq "打开视频") { $script:dlg = $h }
  return $true
}
[void][SetTextNative]::EnumWindows($enum, [IntPtr]::Zero)
Write-Host "dlg=$($script:dlg.ToInt64())"
if ($script:dlg -eq [IntPtr]::Zero) { throw "no dialog" }
$combo = [SetTextNative]::GetDlgItem($script:dlg, 1148)
$ok = [SetTextNative]::GetDlgItem($script:dlg, 1)
Write-Host "combo1148=$($combo.ToInt64()) ok=$($ok.ToInt64())"
$sent = [SetTextNative]::SendMessageW($combo, [SetTextNative]::WM_SETTEXT, [IntPtr]::Zero, $path)
$sb = New-Object System.Text.StringBuilder 1024
[void][SetTextNative]::GetWindowText($combo, $sb, 1024)
Write-Host "after-set ret=$sent text=$($sb.ToString())"
$rect = New-Object SetTextNative+RECT
[void][SetTextNative]::GetWindowRect($script:dlg, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$shot = Join-Path $PSScriptRoot "out\dialog-settext.png"
$bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "shot=$shot"
