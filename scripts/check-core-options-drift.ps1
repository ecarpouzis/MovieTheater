#requires -Version 5.1
<#
.SYNOPSIS
  Fail if the committed core-option catalog has drifted from the DEPLOYED core DLLs.

.DESCRIPTION
  Phase 4.3 of docs/arcade-config-module-dead-options-plan.md. libretro silently ignores unknown
  option keys and value tokens, so catalog/DLL drift is invisible at runtime — a player-facing
  dropdown just quietly stops matching what the core reads. This check makes drift loud.

  It re-runs the Phase 1 extractor (scripts/extract-core-options.ps1) against the deployed cores
  into a TEMP directory, rebuilds the catalog there, and compares it to the committed
  src/MovieTheater/Arcade/core-options-catalog.json ignoring only the "_generated" date stamp.
  Exit 0 = no drift; exit 1 = drift (diff summary printed); exit 2 = the check itself failed.

  MUST run on the machine that hosts the deployed cores (Ziggy — D:\ArcadeStorage). The CI runner
  is NOT that machine, so this is a local/scheduled check, not a workflow step. Read-only against
  D:\ (the extractor LoadLibrary's the DLLs and touches nothing else; live rooms are unaffected).
  Run it after any core deploy, or on a schedule:
    schtasks /Create /TN "MovieTheater - Core Options Drift Check" /SC WEEKLY /D SUN /ST 05:00 ^
      /TR "pwsh -NoProfile -File F:\Work\MovieTheater\scripts\check-core-options-drift.ps1"
  (Registration is left to a human on purpose — it spawns ~40 core-loading child processes.)

.EXAMPLE
  pwsh -File scripts/check-core-options-drift.ps1
#>
[CmdletBinding()]
param(
    [string] $CoresDir = 'D:\ArcadeStorage\worker-gl\assets\cores'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$committed = Join-Path $repo 'src\MovieTheater\Arcade\core-options-catalog.json'
$work = Join-Path ([IO.Path]::GetTempPath()) ("core-options-drift-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    $freshCatalog = Join-Path $work 'catalog.json'
    $freshReport  = Join-Path $work 'report.md'
    # The builder regenerates IN PLACE (--old and --out are the same path): it reads the previous
    # catalog for label/category preservation and the diff, then overwrites it. Seed the temp copy
    # with the committed catalog so "old" is exactly what we're checking drift against.
    Copy-Item $committed $freshCatalog

    # Full re-extraction into the temp dir (-Force is implicit: the dir is empty).
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'extract-core-options.ps1') `
        -CoresDir $CoresDir -OutDir (Join-Path $work 'extract') `
        -Catalog $freshCatalog -Report $freshReport
    if ($LASTEXITCODE -ne 0) { Write-Error "extractor failed (exit $LASTEXITCODE)"; exit 2 }
    if (-not (Test-Path $freshCatalog)) { Write-Error "extractor produced no catalog"; exit 2 }

    # Compare, ignoring only the generation date stamp.
    $strip = { param($path) (Get-Content $path) -notmatch '^\s*"_generated":' }
    $a = & $strip $committed
    $b = & $strip $freshCatalog
    $diff = Compare-Object -ReferenceObject $a -DifferenceObject $b
    if (-not $diff) {
        Write-Host "OK: catalog matches the deployed cores ($CoresDir)."
        exit 0
    }

    Write-Host "DRIFT: committed core-options-catalog.json no longer matches the deployed cores." -ForegroundColor Red
    Write-Host "Changed lines (<= committed, => deployed), first 80:"
    $diff | Select-Object -First 80 | ForEach-Object {
        $marker = if ($_.SideIndicator -eq '<=') { '<=' } else { '=>' }
        Write-Host "  $marker $($_.InputObject)"
    }
    Write-Host "Full fresh catalog + report kept at: $work"
    Write-Host "Fix: re-run scripts/extract-core-options.ps1 -Force, review the diff + drift report, commit."
    exit 1
}
catch {
    Write-Error $_
    exit 2
}
finally {
    # Keep $work only when drift was found (the message above points at it).
    if ($LASTEXITCODE -eq 0 -and (Test-Path $work)) { Remove-Item -Recurse -Force $work }
}
