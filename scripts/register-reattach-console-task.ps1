<#
.SYNOPSIS
    Register the on-demand elevated task "MovieTheater - Reattach Console" that reattaches the arcade
    capture session to the physical console. RUN THIS ONCE, ELEVATED (as admin).

.DESCRIPTION
    Window-mode capture over RDP runs at the RDP display's refresh (~32Hz), and after an RDP *disconnect*
    the session can sit in a stalled disconnected-DWM state. The supported fix is
    `tscon <sessionId> /dest:console`, which restores the physical GPU displays and 60fps capture — but
    tscon needs SeTcbPrivilege (admin/SYSTEM), and the capture worker task is NON-elevated.

    So the worker only TRIGGERS this task (schtasks /Run) when it detects its session is DISCONNECTED;
    the task runs reattach-console.ps1 as SYSTEM (which has the privilege) to do the tscon. This script
    registers that task:
      * runs as SYSTEM (S-1-5-18), RunLevel HIGHEST,
      * ON-DEMAND only (no triggers),
      * with a security descriptor granting Authenticated Users read + run, so the non-elevated worker
        can start it (the whole reason for the split).

    The action runs D:\ArcadeStorage\worker-capture\bin\reattach-console.ps1 (deployed beside the worker;
    that script has its own hard guard: it NEVER moves an Active session, only a Disconnected one).

    Registration uses the Schedule.Service COM API because only it can set the task's SDDL
    (Register-ScheduledTask cannot). If Authenticated-Users-run is ever rejected in your environment,
    the documented fallback is to register the task to run as Eric's account with RunLevel Highest and
    grant that user run rights instead — pick what works and note why.

.PARAMETER Script     The reattach worker script the task runs (deployed beside the capture worker).
.PARAMETER TaskName   Task name (MUST match the worker's constant "MovieTheater - Reattach Console").
.PARAMETER GrantSid   SID granted read+run on the task (default AU = Authenticated Users).
#>
param(
    [string]$Script   = "D:\ArcadeStorage\worker-capture\bin\reattach-console.ps1",
    [string]$TaskName = "MovieTheater - Reattach Console",
    [string]$GrantSid = "AU"
)
$ErrorActionPreference = 'Stop'

# must be elevated
$idn = [Security.Principal.WindowsIdentity]::GetCurrent()
$pr  = New-Object Security.Principal.WindowsPrincipal($idn)
if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This must run ELEVATED. Re-run from an admin PowerShell: powershell -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
}
if (-not (Test-Path $Script)) {
    Write-Warning "reattach script not found yet: $Script — deploy it (build/deploy step) before the first capture room needs it. Registering the task anyway."
}

# TASK_* enums
$TASK_CREATE_OR_UPDATE   = 6
$TASK_LOGON_SERVICE_ACCT = 5
$TASK_RUNLEVEL_HIGHEST   = 1
$TASK_ACTION_EXEC        = 0
$TASK_INSTANCES_IGNORE   = 2   # a second trigger while one runs is dropped

$svc = New-Object -ComObject Schedule.Service
$svc.Connect()
$root = $svc.GetFolder("\")

$def = $svc.NewTask(0)
$def.RegistrationInfo.Description = "Reattach the arcade capture session to the physical console (tscon /dest:console) so a friend gets full-rate 60fps capture when nobody is attached. Triggered on-demand by the non-elevated capture worker; never moves an Active session."
$def.RegistrationInfo.Author = "MovieTheater arcade"
$def.Settings.Enabled                     = $true
$def.Settings.AllowDemandStart            = $true
$def.Settings.Hidden                      = $false
$def.Settings.DisallowStartIfOnBatteries  = $false
$def.Settings.StopIfGoingOnBatteries      = $false
$def.Settings.MultipleInstances           = $TASK_INSTANCES_IGNORE
# The script now waits the dwell out in process (~60 s) instead of stamping a file and exiting, so a run
# is no longer instantaneous. Its OWN -MaxWaitSeconds is the real bound; this limit is only a backstop,
# and it is deliberately far above the dwell — being killed HERE is the bad outcome, because a run cut
# off mid-wait writes no verdict to the log and looks exactly like a run that never happened. Was PT2M,
# sized when the script did nothing but read a file.
$def.Settings.ExecutionTimeLimit          = "PT5M"
$def.Principal.RunLevel  = $TASK_RUNLEVEL_HIGHEST
$def.Principal.UserId    = "S-1-5-18"                # SYSTEM
$def.Principal.LogonType = $TASK_LOGON_SERVICE_ACCT

$act = $def.Actions.Create($TASK_ACTION_EXEC)
$act.Path      = "powershell.exe"
$act.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$Script`""

# SDDL: GrantSid gets Generic Read + Generic Execute (read + run); Admins + SYSTEM get full control.
$sddl = "D:(A;;GRGX;;;$GrantSid)(A;;GA;;;BA)(A;;GA;;;SY)"

# RegisterTaskDefinition(path, definition, flags, userId, password, logonType, sddl)
$root.RegisterTaskDefinition($TaskName, $def, $TASK_CREATE_OR_UPDATE, "S-1-5-18", $null, $TASK_LOGON_SERVICE_ACCT, $sddl) | Out-Null

Write-Host "registered task '$TaskName' (SYSTEM / RunLevel Highest / on-demand; '$GrantSid' may run it)."
Write-Host "verify:  schtasks /Query /TN `"$TaskName`" /V /FO LIST"
Write-Host "test  :  schtasks /Run   /TN `"$TaskName`"   (only tscons a DISCONNECTED session; safe no-op if Active)"
