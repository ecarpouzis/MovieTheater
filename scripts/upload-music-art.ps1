<#
.SYNOPSIS
  Pushes album covers extracted from the music share up to the live images mount.

.DESCRIPTION
  Prod owns the images mount but cannot read the music share (different hosts), and this box is the
  other way round. POST /API/Admin/Music/UploadArt/{id} is the bridge, and it is admin-gated, so this
  has to be run by a human with an admin account -- which is the whole reason this script exists
  rather than being folded into a CLI command.

  The payload is a directory of "<albumId>.png" files, already extracted and vetted. Regenerating it
  is a separate job; this script only uploads what is in front of it.

  Bulk-job rules, because this is a bulk job:
    * BOUNDED    -- -Batch albums per pass, with a pause between passes.
    * RESUMABLE  -- every success is appended to uploaded.log; a re-run skips those. Kill it whenever.
    * IDEMPOTENT -- UploadArt overwrites, so a re-post of the same album is harmless.
    * OBSERVABLE -- prints { done, ok, failed, remaining } after every batch, not just at the end.
  Nothing is deleted, and nothing on the NAS is touched at all -- this only reads the payload dir.

.PARAMETER WhatIf
  Log in, verify admin, count the work, upload NOTHING. Run this first.

.EXAMPLE
  pwsh -File scripts\upload-music-art.ps1 -WhatIf
  pwsh -File scripts\upload-music-art.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ArtDir = 'data\music-art-upload',
    [string]$BaseUrl = 'https://theater.carpouzis.com',
    [string]$Username = 'Eric',
    [int]$Batch = 25,
    [int]$PauseMs = 200,
    [int]$MaxFailures = 15
)

$ErrorActionPreference = 'Stop'

# Resolve the payload relative to the repo, not the shell's working directory.
if (-not [IO.Path]::IsPathRooted($ArtDir)) {
    $repo = Split-Path -Parent $PSScriptRoot
    $ArtDir = Join-Path $repo $ArtDir
}
if (-not (Test-Path -LiteralPath $ArtDir)) { throw "No payload directory at $ArtDir" }

$journal = Join-Path $ArtDir 'uploaded.log'
$failures = Join-Path $ArtDir 'failed.log'

$all = @(Get-ChildItem -LiteralPath $ArtDir -Filter '*.png' | Sort-Object { [int]$_.BaseName })
if ($all.Count -eq 0) { throw "No <albumId>.png files in $ArtDir" }

$done = @{}
if (Test-Path -LiteralPath $journal) {
    foreach ($l in Get-Content -LiteralPath $journal) {
        $t = $l.Trim(); if ($t) { $done[$t] = $true }
    }
}
$todo = @($all | Where-Object { -not $done.ContainsKey($_.BaseName) })

Write-Host ""
Write-Host "payload   : $ArtDir"
Write-Host "covers    : $($all.Count) total, $($done.Count) already uploaded, $($todo.Count) to do"
Write-Host "target    : $BaseUrl"
Write-Host ""
if ($todo.Count -eq 0) { Write-Host "Nothing to do."; return }

# --- log in ---------------------------------------------------------------------------------------
# Read-Host -AsSecureString so the password is never echoed, never in a file, and never in history.
$secure = Read-Host "Password for '$Username'" -AsSecureString
$plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))

$session = $null
try {
    $body = @{ Username = $Username; Password = $plain } | ConvertTo-Json
    $r = Invoke-WebRequest "$BaseUrl/API/Login" -Method POST -ContentType 'application/json' `
        -Body $body -SessionVariable session -TimeoutSec 60
    if ($r.StatusCode -ne 200) { throw "login returned $($r.StatusCode)" }
}
catch { throw "Login failed: $($_.Exception.Message)" }
finally { $plain = $null; [GC]::Collect() }

# Prove the ADMIN half before uploading anything: a password-verified session that is not an admin
# would 403 every single POST, and finding that out 340 requests in helps nobody.
try {
    $probe = Invoke-WebRequest "$BaseUrl/API/Admin/PatchedArtifacts" -WebSession $session -TimeoutSec 60
    Write-Host "login ok, admin confirmed ($($probe.StatusCode))" -ForegroundColor Green
}
catch {
    $code = $_.Exception.Response.StatusCode.value__
    throw "Logged in, but '$Username' is not an admin (admin probe returned $code). UploadArt would 403."
}

if ($WhatIfPreference) {
    Write-Host ""
    Write-Host "WHATIF: would upload $($todo.Count) cover(s) in $([math]::Ceiling($todo.Count / $Batch)) batch(es). Nothing sent."
    return
}

# --- upload ---------------------------------------------------------------------------------------
$sw = [Diagnostics.Stopwatch]::StartNew()
$ok = 0; $bad = 0; $i = 0
foreach ($f in $todo) {
    $i++
    $id = $f.BaseName
    try {
        $bytes = [IO.File]::ReadAllBytes($f.FullName)
        $resp = Invoke-WebRequest "$BaseUrl/API/Admin/Music/UploadArt/$id" -Method POST `
            -WebSession $session -ContentType 'application/octet-stream' -Body $bytes -TimeoutSec 120
        if ($resp.StatusCode -eq 200) {
            Add-Content -LiteralPath $journal -Value $id   # journal FIRST-class: this is the resume point
            $ok++
        }
        else { $bad++; Add-Content -LiteralPath $failures -Value "$id http $($resp.StatusCode)" }
    }
    catch {
        $bad++
        $code = $_.Exception.Response.StatusCode.value__
        Add-Content -LiteralPath $failures -Value "$id $(if($code){"http $code"}else{$_.Exception.Message})"
    }

    if ($bad -ge $MaxFailures) {
        Write-Warning "Stopping: $bad failures (see $failures). Nothing already uploaded is lost -- re-run to continue."
        break
    }
    Start-Sleep -Milliseconds $PauseMs

    if ($i % $Batch -eq 0 -or $i -eq $todo.Count) {
        Write-Host ("[{0}] done={1,-4} ok={2,-4} failed={3,-3} remaining={4,-4} elapsed={5:mm\:ss}" -f `
            (Get-Date -Format 'HH:mm:ss'), $i, $ok, $bad, ($todo.Count - $i), $sw.Elapsed)
    }
}

Write-Host ""
Write-Host "uploaded $ok, failed $bad, elapsed $($sw.Elapsed.ToString('hh\:mm\:ss'))"
Write-Host "resume journal: $journal"
if ($bad -gt 0) { Write-Host "failures      : $failures" -ForegroundColor Yellow }
Write-Host ""
Write-Host "Spot-check a few in a browser before trusting the batch:" -ForegroundColor Cyan
foreach ($s in ($todo | Select-Object -First 3)) { Write-Host "  $BaseUrl/MusicImageThumb/$($s.BaseName)" }
