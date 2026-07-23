<#
.SYNOPSIS
    Reattach the arcade capture session to the PHYSICAL CONSOLE (tscon /dest:console).

.DESCRIPTION
    Deployed beside the capture worker (D:\ArcadeStorage\worker-capture\bin) and run ON-DEMAND, as
    SYSTEM, by the scheduled task "MovieTheater - Reattach Console" (register-reattach-console-task.ps1).
    The non-elevated capture worker triggers that task at room start when it detects its own session is
    RDP-DISCONNECTED; tscon then moves that session back to the physical console so the game runs on the
    real high-refresh displays and WGC capture is full-rate (60fps) instead of the stalled/32Hz
    disconnected-DWM rate. tscon needs SeTcbPrivilege to move a session without a password, which is why
    the task runs as SYSTEM.

    Target session resolution:
      1. The worker writes its session id to `.reattach-session` beside this script — used if present.
      2. Otherwise, auto-detect the SINGLE Disconnected session (refuse if there are several).
    A hard guard never moves an ACTIVE session (that would kick an attached user to the console).
#>
$ErrorActionPreference = 'Continue'
$log = 'D:\ArcadeStorage\logs\reattach-console.log'
try { New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null } catch {}
function Log($m) { $ts = (Get-Date -Format o); try { Add-Content -Path $log -Value "$ts  $m" -Encoding UTF8 } catch {}; Write-Host $m }

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class WTS {
  [DllImport("wtsapi32.dll", SetLastError=true)] public static extern int WTSEnumerateSessions(IntPtr h,int reserved,int ver,out IntPtr ppSessionInfo,out int count);
  [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct WTS_SESSION_INFO { public int SessionId; [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName; public int State; }
}
"@

function Get-Sessions {
  $pp = [IntPtr]::Zero; $cnt = 0
  if ([WTS]::WTSEnumerateSessions([IntPtr]::Zero, 0, 1, [ref]$pp, [ref]$cnt) -eq 0) { Log "WTSEnumerateSessions failed"; return @() }
  $sz = [Runtime.InteropServices.Marshal]::SizeOf([type][WTS+WTS_SESSION_INFO]); $list = @()
  for ($i = 0; $i -lt $cnt; $i++) {
    $cur = [IntPtr]($pp.ToInt64() + $i * $sz)
    $list += [Runtime.InteropServices.Marshal]::PtrToStructure($cur, [type][WTS+WTS_SESSION_INFO])
  }
  [WTS]::WTSFreeMemory($pp); return $list
}

# WTS_CONNECTSTATE_CLASS: 0=Active, 4=Disconnected.
$sessions = Get-Sessions
Log ("sessions: " + (($sessions | ForEach-Object { "id=$($_.SessionId) state=$($_.State) name=$($_.pWinStationName)" }) -join ' | '))

$target = $null
$sentinel = Join-Path $PSScriptRoot '.reattach-session'
if (Test-Path $sentinel) {
  $raw = (Get-Content $sentinel -Raw).Trim()
  if ($raw -match '^\d+$') { $target = [int]$raw; Log "target from sentinel: session $target" }
}
if ($null -eq $target) {
  $disc = @($sessions | Where-Object { $_.State -eq 4 })
  if ($disc.Count -eq 1) { $target = $disc[0].SessionId; Log "auto-detected single disconnected session $target" }
  elseif ($disc.Count -gt 1) { Log "REFUSING: multiple disconnected sessions ($(($disc | ForEach-Object SessionId) -join ',')); need the worker sentinel"; exit 2 }
}
if ($null -eq $target) { Log "no target (no sentinel, no single disconnected session) — nothing to do"; exit 0 }

$ts = $sessions | Where-Object { $_.SessionId -eq $target } | Select-Object -First 1
if ($ts -and $ts.State -eq 0) { Log "target session $target is ACTIVE — NOT reattaching (would kick the attached user)"; exit 0 }

Log "running: tscon $target /dest:console"
$out = & tscon $target /dest:console 2>&1
Log ("tscon exit=$LASTEXITCODE out=$out")
exit $LASTEXITCODE
