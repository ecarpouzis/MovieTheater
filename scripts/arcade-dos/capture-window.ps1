# Saves a PNG of a top-level window (default: the first DOSBox window) without focusing it — used to
# see where an unattended installer in build-dos-base/build-dos-game is stuck.
param([string]$ProcessName = 'DOSBox', [Parameter(Mandatory)][string]$OutFile)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class WinCap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
'@
$p = Get-Process -Name $ProcessName | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
if (-not $p) { throw "no $ProcessName window" }
$r = New-Object WinCap+RECT
[void][WinCap]::GetWindowRect($p.MainWindowHandle, [ref]$r)
$bmp = New-Object Drawing.Bitmap ([Math]::Max(1, $r.R - $r.L)), ([Math]::Max(1, $r.B - $r.T))
$g = [Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[void][WinCap]::PrintWindow($p.MainWindowHandle, $hdc, 2)
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($OutFile, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
"$($p.MainWindowTitle) -> $OutFile"
