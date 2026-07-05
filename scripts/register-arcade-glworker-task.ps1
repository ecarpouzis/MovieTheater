<#
.SYNOPSIS
    Registers the logon Scheduled Task that keeps the Windows-native arcade GL worker running
    (roadmap WS-B, docs/arcade-windows-worker.md). Sibling of register-arcade-wsl-task.ps1.

.DESCRIPTION
    WGL hardware acceleration needs an INTERACTIVE session — a session-0 Windows service gets
    software-only GL and defeats the whole point. So, exactly like the WSL keepalive, this runs in
    the user's interactive logon session: the task launches scripts/run-arcade-glworker.ps1, which
    sets the CLOUD_GAME_* env, prepends the MSYS2 UCRT64 bin to PATH, and loops worker.exe.

    Run ONCE as the user who logs in on Ziggy (no admin needed for an interactive-logon task).
    Prereqs: the worker built (worker.exe), config staged at the ConfDir, MSYS2 installed. Re-run to update.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File register-arcade-glworker-task.ps1
#>
param(
    [string]$RunScript = (Join-Path $PSScriptRoot "run-arcade-glworker.ps1")
)

$TaskName  = "MovieTheater - Arcade GL Worker"
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

# conhost --headless keeps it windowless, same trick as the WSL keepalive task.
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$RunScript`""

$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit  (New-TimeSpan -Seconds 0) `
    -StartWhenAvailable `
    -MultipleInstances   IgnoreNew `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Register-ScheduledTask `
    -TaskName    $TaskName `
    -Action      $action `
    -Trigger     $trigger `
    -Settings    $settings `
    -Principal   $principal `
    -Description "Runs the Windows-native arcade GL worker (WGL/NVIDIA GL for PSP/DC/Naomi/Atomiswave) in the interactive session so it survives logoff/reboot. Managed by register-arcade-glworker-task.ps1." | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "Task '$TaskName' registered and started. Tail the log to confirm it boots + registers zone=gl."
