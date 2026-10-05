<#
.SYNOPSIS
  Installs libretro MAME's static support files into every GL worker: the romset's BIOS/device zips and the
  sound samples. Copy-only and idempotent (robocopy, newer/missing files only, never /PURGE or /MIR).

.DESCRIPTION
  libretro MAME's rompath is "<game's folder>;<system>/mame/bios;<system>/mame/roms" and its samplepath is
  "<system>/mame/samples" (src/osd/libretro/libretro-internal/retro_init.cpp, Set_Path_Option). The site's
  "mame" system ingests NON-MERGED romsets, which are whole except for BIOS and device ROMs — so those live
  here, once per worker, instead of being staged beside every game by the ROM cache. Each worker has its
  own ConfDir (libretro\system is a real per-worker copy, never a junction — arcade skill), so all get one.

  Re-run after moving to a new MAME version: a new romset version brings new/changed BIOS and device sets.

.EXAMPLE
  .\install-mame-system-files.ps1 -BiosDevices 'M:\0 - Downloads\!MAME\MAME 0.289 ROMs (bios-devices)' `
                                  -Samples 'M:\0 - Downloads\!MAME\MAME 0.288 EXTRAs\samples'
#>
param(
  [Parameter(Mandatory)] [string] $BiosDevices,
  [string] $Samples,
  [string[]] $Workers = @('D:\ArcadeStorage\worker-gl', 'D:\ArcadeStorage\worker-gl-2')
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $BiosDevices -PathType Container)) { throw "BIOS/devices folder not found: $BiosDevices" }
if ($Samples -and -not (Test-Path -LiteralPath $Samples -PathType Container)) { throw "Samples folder not found: $Samples" }

foreach ($w in $Workers) {
  $sys = Join-Path $w 'libretro\system'
  if (-not (Test-Path -LiteralPath $sys -PathType Container)) { Write-Warning "skip $w (no libretro\system)"; continue }
  $jobs = @(@{ From = $BiosDevices; To = Join-Path $sys 'mame\bios'; What = '*.zip' })
  if ($Samples) { $jobs += @{ From = $Samples; To = Join-Path $sys 'mame\samples'; What = '*.zip' } }
  foreach ($j in $jobs) {
    New-Item -ItemType Directory -Force -Path $j.To | Out-Null
    # /XO: skip files the destination already has newer; /R:2 /W:5: a NAS hiccup retries briefly instead of
    # hanging; /NP /NFL /NDL keep the log to the summary. Exit codes < 8 are success for robocopy.
    robocopy $j.From $j.To $j.What /XO /R:2 /W:5 /NP /NFL /NDL /NJH | Select-Object -Last 6
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE) copying $($j.From) -> $($j.To)" }
    $n = (Get-ChildItem -LiteralPath $j.To -Filter $j.What -File).Count
    Write-Host ("{0}: {1} file(s) in {2}" -f (Split-Path $w -Leaf), $n, $j.To)
  }
}
$global:LASTEXITCODE = 0
