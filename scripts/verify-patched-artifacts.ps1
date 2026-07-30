<#
.SYNOPSIS
    Detect when a PATCHED or PINNED binary has been reverted, replaced, or lost - across every
    system (arcade cores + the native Jellyfin), before it silently changes behaviour.

.DESCRIPTION
    We run a growing set of binaries that are NOT what their upstream ships: hand-built cores,
    byte-patched cores, cores pinned to one specific buildbot nightly, and a patched Jellyfin.
    Every one of them has been (or can be) reverted with NO error and NO log line. Two proven
    mechanisms:

      1. ARCADE CORES. `cores.repo.sync: true` pulls from buildbot.libretro.com/nightly on EVERY
         worker start. It is presence-only: manager/http.go diff() compares core NAMES against
         installed NAMES - no hash, no version, no timestamp. So an existing DLL is never
         overwritten (that is the only reason our patches have survived), but ANY ABSENT FILE IS
         SILENTLY REPLACED WITH STOCK. A fresh worker ConfDir, an added worker, or someone deleting
         a "corrupt" DLL to force a refresh all quietly de-patch the fleet. The danger is worst for
         patched cores that keep their STOCK FILENAME, because the buildbot has that exact name.
      2. JELLYFIN. Any stock Jellyfin upgrade overwrites the 3 patched DLLs, bringing back the HLS
         copy freeze storms. Nothing in Jellyfin warns about this.

    This script is the tripwire. It is READ-ONLY by default: it hashes what is on disk, compares to
    the committed manifest, and reports. It NEVER writes a binary unless you explicitly pass
    -Restore, and -Restore refuses to guess (it only ever copies bytes we ourselves vaulted).

    Findings, most to least severe:
      MISSING    - a file the manifest expects is gone. For a stock-named core this means the next
                   worker start installs STOCK over it. Treat as an outage.
      DRIFT      - the file exists but its sha256 differs from the manifest. Either it was reverted
                   or it was rebuilt intentionally (if intentional, re-run with -Snapshot).
      DISAGREE   - the same logical artifact differs BETWEEN deploy locations (e.g. worker-gl vs
                   worker-gl-2). Its own bug class: half the fleet runs different code, so a room
                   behaves differently depending on which worker the coordinator picked.
      OK         - byte-identical to the manifest.

.PARAMETER Snapshot
    Record the CURRENT on-disk bytes as the expected state, rewriting the manifest. Use this after
    an INTENTIONAL rebuild/redeploy, never to silence a finding you have not explained - that would
    bless a revert as the new truth. Also refreshes the vault unless -NoVault.

.PARAMETER Restore
    Copy vaulted bytes back over MISSING/DRIFT files. Requires a vault entry for the artifact;
    skips anything it cannot prove. Prints what it would do and requires -Confirm:$false to skip
    the prompt. Workers must be recycled afterwards - cores load at process start.

.PARAMETER Json
    Emit machine-readable results (for the watchdog) instead of the human report.

.PARAMETER NoVault
    With -Snapshot, update hashes only; do not copy bytes into the vault.

.NOTES
    Kept ASCII-only on purpose: this runs from a scheduled task under PowerShell 5.1, where a .ps1
    containing non-ASCII must be UTF-8 WITH BOM or the parser mangles it.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch] $Snapshot,
    [switch] $Restore,
    [switch] $Json,
    [switch] $NoVault,
    [string] $ManifestPath = (Join-Path $PSScriptRoot 'patched-artifacts.json'),
    [string] $VaultDir     = 'D:\ArcadeStorage\patched-vault'
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# The artifact catalogue. Each entry is a LOGICAL artifact that may be deployed to several paths.
# 'roots' are searched for 'file'; only locations where the file exists at -Snapshot time are
# recorded, so a later disappearance is reported as MISSING rather than silently ignored.
#
# stockName=$true means upstream ships a file with this EXACT name, so repo.sync can replace it ->
# a missing file degrades SILENTLY. stockName=$false (our *_custom_* naming) fails loudly instead.
# ---------------------------------------------------------------------------------------------
$CORE_ROOTS = @(
    'D:\ArcadeStorage\worker-gl\assets\cores',
    'D:\ArcadeStorage\worker-gl-2\assets\cores',
    'D:\Arcade\build\cloud-game-gl\assets\cores'
)

$CATALOGUE = @(
    @{ id='core-mupen64plus-next'; file='mupen64plus_next_libretro.dll'; roots=$CORE_ROOTS; stockName=$true
       provenance='MT-PATCHED: adds core option mupen64plus-AllowUnalignedDMA (honours odd PI cart addresses; SM64 Last Impact requires it). Patch = pi_controller.c PI_CART_ADDR_ALIGN_MASK + libretro.c global + core option, plus a _CRT_RAND_S build fix.'
       rebuild='See memory mupen64plus-next-core-build: clone libretro/mupen64plus-libretro-nx, apply patch, build from PowerShell with $env:MSYSTEM=MINGW64 + nasm, make platform=win -j8.' },

    @{ id='core-mednafen-psx-hw'; file='mednafen_psx_hw_libretro.dll'; roots=$CORE_ROOTS; stockName=$true
       provenance='PINNED to the buildbot nightly dated 2026-07-16 (fixes the SotN dog-death black-rectangle flash). NOT the newest nightly - do not "update" it without re-running the sotn-dog-repro A/B.'
       rebuild='Re-download that specific nightly; rollback copy at *.pre-nightly.bak.' },

    @{ id='core-kronos'; file='kronos_libretro.dll'; roots=$CORE_ROOTS; stockName=$true
       provenance='NON-BUILDBOT build (live 21 MB vs the 7.7 MB buildbot build kept at *.buildbot.bak). Saturn.'
       rebuild='Unknown - the buildbot copy is at *.buildbot.bak. Confirm provenance before ever replacing.' },

    @{ id='core-citra-custom'; file='citra_custom_libretro.dll'; roots=$CORE_ROOTS; stockName=$false
       provenance='CUSTOM 3DS build (MSVC; docker/arcade/citra-msvc-vs18.patch) + loose update/translation patch support.'
       rebuild='docker/arcade/citra-msvc-vs18.patch' },

    @{ id='core-dolphin-custom'; file='dolphin_custom_libretro.dll'; roots=$CORE_ROOTS; stockName=$false
       provenance='CUSTOM GameCube/Wii build: docker/arcade/dolphin-createsharedcontext.patch (implements CreateSharedContext; enables async ubershaders).'
       rebuild='docker/arcade/dolphin-createsharedcontext.patch' },

    @{ id='core-flycast-custom'; file='flycast_custom_libretro.dll'; roots=$CORE_ROOTS; stockName=$false
       provenance='CUSTOM Dreamcast/Naomi build: flycast-custom-core.patch (Vulkan + OIT lockstep).'
       rebuild='docker/arcade/flycast-custom-core.patch' },

    @{ id='core-pcsx2-custom'; file='pcsx2_custom_libretro.dll'; roots=$CORE_ROOTS; stockName=$false
       provenance='BYTE-PATCHED LRPS2: one GameDB data edit (Stuntman SLUS-20250 round/clamp modes) reachable ONLY via the embedded GameDB.'
       rebuild='docker/arcade/lrps2-perfattr-and-gamedb.patch + lrps2-patch-gamedb.py' },

    @{ id='core-ppsspp-custom'; file='ppsspp_custom_libretro.dll'; roots=$CORE_ROOTS; stockName=$false
       provenance='CUSTOM PSP build: docker/arcade/ppsspp-custom-core.patch (patched MachineContext AV-rescue handler; permits real JIT + fastmem).'
       rebuild='docker/arcade/ppsspp-build-core.bat (MSVC + libretro/Makefile; NEVER MinGW for PPSSPP)' },

    @{ id='jellyfin-api'; file='Jellyfin.Api.dll'; roots=@('C:\Program Files\Jellyfin\Server'); stockName=$true
       provenance='PATCHED Jellyfin 10.11.11: exact per-keyframe HLS copy segmentation + POST /Videos/{itemId}/ExtractKeyframes. Binding identity MUST stay 12.0.0.0.'
       rebuild='.claude/skills/hls-copy-freeze/tools/build-jellyfin-patch.ps1 then deploy-jellyfin-patch.ps1 (elevated)' },

    @{ id='jellyfin-hls'; file='Jellyfin.MediaEncoding.Hls.dll'; roots=@('C:\Program Files\Jellyfin\Server'); stockName=$true
       provenance='PATCHED Jellyfin 10.11.11 (HLS segmentation). Binding identity MUST stay 12.0.0.0.'
       rebuild='.claude/skills/hls-copy-freeze/tools/build-jellyfin-patch.ps1' },

    @{ id='jellyfin-controller'; file='MediaBrowser.Controller.dll'; roots=@('C:\Program Files\Jellyfin\Server'); stockName=$true
       provenance='PATCHED Jellyfin 10.11.11. Binding identity MUST stay 10.11.11.0.'
       rebuild='.claude/skills/hls-copy-freeze/tools/build-jellyfin-patch.ps1' }
)

function Get-Sha([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }

# NOTE: deliberately NOT a function. PowerShell's array-return semantics bit twice here - a
# single-element return unwrapped to a bare string (so $locs[0] indexed the first CHARACTER of a
# path, "C"), and the ,$found fix then over-wrapped it (so one "path" was the whole array,
# stringified into a single bogus filename). Inline enumeration has no such ambiguity.

# ------------------------------------------------------------------ SNAPSHOT
if ($Snapshot) {
    $man = [ordered]@{
        generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        note         = 'Expected bytes for every patched/pinned binary. Regenerate ONLY after an intentional rebuild (verify-patched-artifacts.ps1 -Snapshot). Never regenerate to silence an unexplained finding.'
        artifacts    = @()
    }
    if (-not $NoVault -and -not (Test-Path $VaultDir)) { New-Item -ItemType Directory -Path $VaultDir -Force | Out-Null }

    foreach ($e in $CATALOGUE) {
        $locs = @()
        foreach ($r in $e.roots) {
            $cand = Join-Path $r $e.file
            if (Test-Path -LiteralPath $cand) { $locs += $cand }
        }
        if ($locs.Count -eq 0) {
            Write-Warning "$($e.id): NOT FOUND in any known location - recorded with no locations, verify will flag it."
        }
        $hashes = @{}
        foreach ($l in $locs) { $hashes[$l] = Get-Sha $l }
        $distinct = @($hashes.Values | Sort-Object -Unique)
        if ($distinct.Count -gt 1) {
            Write-Warning "$($e.id): locations DISAGREE at snapshot time - fix the fleet before trusting this manifest:"
            foreach ($k in $hashes.Keys) { Write-Warning "    $($hashes[$k].Substring(0,16))  $k" }
        }
        $primary = if ($distinct.Count -ge 1) { $distinct[0] } else { $null }

        $vaulted = $null
        if (-not $NoVault -and $locs.Count -gt 0) {
            $vaulted = Join-Path $VaultDir "$($e.id)__$($e.file)"
            Copy-Item -LiteralPath $locs[0] -Destination $vaulted -Force
        }

        $man.artifacts += [ordered]@{
            id         = $e.id
            file       = $e.file
            stockName  = [bool]$e.stockName
            sha256     = $primary
            size       = if ($locs.Count) { (Get-Item -LiteralPath $locs[0]).Length } else { $null }
            locations  = $locs
            vault      = $vaulted
            provenance = $e.provenance
            rebuild    = $e.rebuild
        }
    }
    $man | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
    Write-Host "[artifacts] snapshot written: $ManifestPath ($($man.artifacts.Count) artifacts)"
    if (-not $NoVault) { Write-Host "[artifacts] vault refreshed: $VaultDir" }
    return
}

# ------------------------------------------------------------------ VERIFY
if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "manifest not found: $ManifestPath  (create it once with -Snapshot, after confirming the current binaries are the ones you intend to run)"
}
$man = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$findings = @()

foreach ($a in $man.artifacts) {
    $cat = $CATALOGUE | Where-Object { $_.id -eq $a.id }
    $expectedLocs = @($a.locations)
    if ($expectedLocs.Count -eq 0) {
        $findings += [pscustomobject]@{ id=$a.id; status='MISSING'; path='(none recorded)'; detail='manifest records no location'; stockName=$a.stockName }
        continue
    }
    $seen = @{}
    foreach ($p in $expectedLocs) {
        if (-not (Test-Path -LiteralPath $p)) {
            $findings += [pscustomobject]@{ id=$a.id; status='MISSING'; path=$p; detail='file absent'; stockName=$a.stockName }
            continue
        }
        $h = Get-Sha $p
        $seen[$p] = $h
        if ($h -ne $a.sha256) {
            $findings += [pscustomobject]@{ id=$a.id; status='DRIFT'; path=$p
                detail="expected $($a.sha256.Substring(0,16))... got $($h.Substring(0,16))..."; stockName=$a.stockName }
        }
    }
    $distinct = @($seen.Values | Sort-Object -Unique)
    if ($distinct.Count -gt 1) {
        $findings += [pscustomobject]@{ id=$a.id; status='DISAGREE'; path=($seen.Keys -join ' | ')
            detail="$($distinct.Count) different builds deployed for one artifact"; stockName=$a.stockName }
    }
}

# ------------------------------------------------------------------ RESTORE
if ($Restore -and $findings.Count -gt 0) {
    foreach ($f in ($findings | Where-Object { $_.status -in 'MISSING','DRIFT' })) {
        $a = $man.artifacts | Where-Object { $_.id -eq $f.id }
        if (-not $a.vault -or -not (Test-Path -LiteralPath $a.vault)) {
            Write-Warning "[restore] $($f.id): NO vault copy - refusing to guess. Rebuild instead: $($a.rebuild)"
            continue
        }
        if ((Get-Sha $a.vault) -ne $a.sha256) {
            Write-Warning "[restore] $($f.id): vault copy does not match the manifest hash - refusing (vault itself is suspect)."
            continue
        }
        if ($f.path -eq '(none recorded)') { continue }
        if ($PSCmdlet.ShouldProcess($f.path, "restore patched bytes from vault")) {
            Copy-Item -LiteralPath $a.vault -Destination $f.path -Force
            Write-Host "[restore] $($f.id) -> $($f.path)"
        }
    }
    Write-Host "[restore] done. RECYCLE THE WORKERS (cores load at process start): scripts\recycle-arcade-glworker.ps1 -WorkerId 1 (then 2). Jellyfin: restart its service."
}

# ------------------------------------------------------------------ REPORT
if ($Json) {
    [pscustomobject]@{
        checkedUtc = (Get-Date).ToUniversalTime().ToString('o')
        artifacts  = $man.artifacts.Count
        findings   = $findings
        ok         = ($findings.Count -eq 0)
    } | ConvertTo-Json -Depth 5
} else {
    $paths = ($man.artifacts | ForEach-Object { @($_.locations).Count } | Measure-Object -Sum).Sum
    Write-Host "[artifacts] $($man.artifacts.Count) artifacts / $paths deployed paths checked against $(Split-Path $ManifestPath -Leaf)"
    if ($findings.Count -eq 0) {
        Write-Host "[artifacts] OK - every patched/pinned binary is byte-identical to the manifest."
    } else {
        Write-Host ""
        Write-Host "[artifacts] $($findings.Count) FINDING(S):"
        foreach ($f in ($findings | Sort-Object { switch ($_.status) { 'MISSING' {0} 'DISAGREE' {1} 'DRIFT' {2} default {3} } })) {
            $flag = if ($f.stockName -and $f.status -eq 'MISSING') { '  <== STOCK NAME: next worker start will install STOCK over it' } else { '' }
            Write-Host ("  {0,-9} {1,-24} {2}{3}" -f $f.status, $f.id, $f.path, $flag)
            Write-Host ("            {0}" -f $f.detail)
        }
        Write-Host ""
        Write-Host "[artifacts] If a finding is an INTENTIONAL rebuild: re-run with -Snapshot."
        Write-Host "[artifacts] If it is a revert: -Restore (vaulted bytes only), then recycle workers / restart Jellyfin."
    }
}
exit $(if ($findings.Count -gt 0) { 1 } else { 0 })
