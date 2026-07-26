<#
.SYNOPSIS
    Mint the connect token for the arcade's SITE RetroAchievements service account and write it to the
    worker's local secret file. Run once (and again only if you rotate the account).

.DESCRIPTION
    The arcade runs ONE RA account as its scoring engine: it fetches each game's achievement/leaderboard
    definitions and evaluates them locally, in SPECTATOR mode, so it never submits/earns anything on RA.
    That account needs a durable "connect token" (not its password) — this logs in once to obtain it and
    stores username + token in D:\ArcadeStorage\secrets\arcade-ra-site-account.txt, which the GL-worker
    runner reads into CLOUD_GAME_RETROACHIEVEMENTS_SITEUSER / _SITETOKEN. The password is used only for
    this one call and never stored, printed, or committed.

    Recommended: use a DEDICATED account (e.g. "CarpouzisArcade"), not a personal one.

    After running, redeploy the workers (they read the file on start):
        scripts\recycle-arcade-glworker.ps1 -WorkerId 1   (and 2)   — or the full stop/swap/restart.

.PARAMETER Username   The RA service account's username.
.PARAMETER OutFile    Where to write "username`ntoken". Defaults to the worker secret path.
#>
param(
    [Parameter(Mandatory)][string] $Username,
    [string] $OutFile = "D:\ArcadeStorage\secrets\arcade-ra-site-account.txt"
)
$ErrorActionPreference = "Stop"

$sec = Read-Host -AsSecureString "Password for RA account '$Username' (used once, never stored)"
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
try { $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }

Write-Host "logging in to RetroAchievements as '$Username'..."
$body = @{ u = $Username; p = $password }
try {
    $resp = Invoke-RestMethod -Method Post -Uri "https://retroachievements.org/dorequest.php?r=login2" -Body $body
} catch {
    throw "RA login request failed: $($_.Exception.Message)"
}
$password = $null

if (-not $resp.Success -or [string]::IsNullOrWhiteSpace($resp.Token)) {
    throw "RA rejected the credentials (Success=$($resp.Success)). Check the username/password."
}
$canonUser = if ($resp.User) { $resp.User } else { $Username }

New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
# username on line 1, connect token on line 2 — the exact shape run-arcade-glworker.ps1 reads.
[System.IO.File]::WriteAllText($OutFile, "$canonUser`n$($resp.Token)`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host "SUCCESS — wrote site RA account '$canonUser' (token length $($resp.Token.Length)) to:"
Write-Host "  $OutFile"
Write-Host "Now redeploy the workers so they pick it up (stop/swap/restart, or recycle-arcade-glworker.ps1 -WorkerId 1 & 2)."
