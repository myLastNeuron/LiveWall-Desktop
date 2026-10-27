# Capture only the LiveWall window into a PNG (does not capture the rest of the screen).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class Cap {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static string ClassOf(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static List<IntPtr> WindowsOf(uint pid) {
    var found = new List<IntPtr>();
    EnumChildWindows(GetDesktopWindow(), (h, p) => {
      uint q; GetWindowThreadProcessId(h, out q);
      if (q == pid) found.Add(h);
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

$pid_ = (Get-Process LiveWall -ErrorAction Stop).Id
$target = $null; $bestArea = 0
foreach ($h in [Cap]::WindowsOf([uint32]$pid_)) {
  if (-not [Cap]::ClassOf($h).StartsWith('WindowsForms10')) { continue }
  $r = New-Object Cap+RECT
  [void][Cap]::GetWindowRect($h, [ref]$r)
  $area = ($r.R - $r.L) * ($r.B - $r.T)
  if ($area -gt $bestArea) { $bestArea = $area; $target = $h; $rect = $r }
}
if (-not $target) { throw 'no WindowForms window found for LiveWall' }

$w = $rect.R - $rect.L; $ht = $rect.B - $rect.T
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# 2 = PW_RENDERFULLCONTENT, needed for DirectComposition-backed content
$ok = [Cap]::PrintWindow($target, $hdc, 2)
$g.ReleaseHdc($hdc); $g.Dispose()
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'wallpaper-window.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "hwnd=0x$('{0:X}' -f [int64]$target) size=${w}x${ht} PrintWindow=$ok -> $out"
