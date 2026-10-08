$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class EnumNative {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
$proc = Get-Process MTXXVideo -ErrorAction SilentlyContinue
Write-Host ("videoPid=" + ($(if ($proc) { $proc.Id } else { "none" })))
$pidWant = 0
if ($proc) { $pidWant = $proc.Id }
$script:rows = New-Object System.Collections.Generic.List[string]
$enum = [EnumNative+EnumProc]{
  param($h, $l)
  $procId = [uint32]0
  [void][EnumNative]::GetWindowThreadProcessId($h, [ref]$procId)
  if ([int]$procId -ne $pidWant) { return $true }
  $title = New-Object System.Text.StringBuilder 512
  $cls = New-Object System.Text.StringBuilder 256
  [void][EnumNative]::GetWindowText($h, $title, $title.Capacity)
  [void][EnumNative]::GetClassName($h, $cls, $cls.Capacity)
  $vis = [EnumNative]::IsWindowVisible($h)
  $rect = New-Object EnumNative+RECT
  [void][EnumNative]::GetWindowRect($h, [ref]$rect)
  $script:rows.Add(("TOP vis={0} hwnd={1} class={2} title={3} {4},{5} {6}x{7}" -f $vis, $h.ToInt64(), $cls, $title, $rect.Left, $rect.Top, ($rect.Right-$rect.Left), ($rect.Bottom-$rect.Top))) | Out-Null
  $childEnum = [EnumNative+EnumProc]{
    param($ch, $cl)
    $ct = New-Object System.Text.StringBuilder 256
    $cc = New-Object System.Text.StringBuilder 256
    [void][EnumNative]::GetWindowText($ch, $ct, $ct.Capacity)
    [void][EnumNative]::GetClassName($ch, $cc, $cc.Capacity)
    $cv = [EnumNative]::IsWindowVisible($ch)
    if ($ct.Length -gt 0 -or $cc.ToString() -match "32770|Edit|Button|Combo") {
      $script:rows.Add(("  CHILD vis={0} hwnd={1} class={2} title={3}" -f $cv, $ch.ToInt64(), $cc, $ct)) | Out-Null
    }
    return $true
  }
  [void][EnumNative]::EnumChildWindows($h, $childEnum, [IntPtr]::Zero)
  return $true
}
[void][EnumNative]::EnumWindows($enum, [IntPtr]::Zero)
$script:rows | ForEach-Object { Write-Host $_ }
# also any visible window whose title contains the open-video phrase
Write-Host "---- title scan ----"
$enum2 = [EnumNative+EnumProc]{
  param($h, $l)
  if (-not [EnumNative]::IsWindowVisible($h)) { return $true }
  $title = New-Object System.Text.StringBuilder 512
  [void][EnumNative]::GetWindowText($h, $title, $title.Capacity)
  $t = $title.ToString()
  if ($t -match "打开视频|打开") {
    $cls = New-Object System.Text.StringBuilder 256
    [void][EnumNative]::GetClassName($h, $cls, $cls.Capacity)
    $procId = [uint32]0
    [void][EnumNative]::GetWindowThreadProcessId($h, [ref]$procId)
    Write-Host ("MATCH pid={0} class={1} title={2}" -f $procId, $cls, $t)
  }
  return $true
}
[void][EnumNative]::EnumWindows($enum2, [IntPtr]::Zero)
