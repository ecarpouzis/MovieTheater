<#
.SYNOPSIS
    One-time setup: registers a Scheduled Task that keeps the Ubuntu-24.04 WSL distro
    (and therefore the arcade CloudRetro GPU stack) running on Ziggy.

.DESCRIPTION
    The arcade workers run in docker INSIDE the Ubuntu-24.04 WSL2 distro (GPU rendering
    needs a real WSLg distro — see docker/arcade/docker-compose.gpu.yml). WSL terminates
    a distro shortly after its last Windows-side client exits, killing the stack — this
    was the recurring "arcade randomly died" failure. The fix is a logon task that holds
    a `sleep infinity` client open forever:

        distro stays up -> systemd stays up -> docker.service (enabled) stays up
        -> the arcade containers (restart: unless-stopped) stay up / come back at logon.

    Run ONCE as the user who owns the WSL distro (no admin needed). Re-run to update.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File register-arcade-wsl-task.ps1
#>
param(
    [string]$Distro = "Ubuntu-24.04"
)

$TaskName = "MovieTheater - Arcade WSL Stack"

# Interactive logon task = registerable without elevation. conhost --headless keeps it windowless.
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument "--headless $env:SystemRoot\System32\wsl.exe -d $Distro --exec sleep infinity"

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

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
    -Description "Holds the $Distro WSL distro open so the arcade CloudRetro GPU stack (docker inside the distro) survives idle timeouts and comes back after reboot+logon. Managed by register-arcade-wsl-task.ps1." | Out-Null

Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 3
$state = (Get-ScheduledTask -TaskName $TaskName).State
Write-Host "Task '$TaskName' registered; state: $state (should be Running)"
wsl.exe --list --running
