#requires -Version 5.1
<#
.SYNOPSIS
  Regenerate the arcade per-game config module's core-option catalog from the DEPLOYED core DLLs.

.DESCRIPTION
  Phase 1 of docs/arcade-config-module-dead-options-plan.md. The catalog's own header has always said
  "regenerate from the DLLs on Ziggy" and there has never been a generator; this is it.

  Method is a RUNTIME HARNESS, not a static parse. scripts/extract-core-options (a small .NET 10 x64
  console tool) LoadLibrary's ONE core per invocation and hands it a retro_environment_t that answers
  RETRO_ENVIRONMENT_GET_CORE_OPTIONS_VERSION = 2 and captures whichever registration command the core
  uses. The core hands over the real structs, which sidesteps the linker string-pooling trap that made
  the earlier static read of pcsx2_renderer wrong (the withdrawn D4 claim).

  ONE CHILD PROCESS PER CORE, on purpose:
    * a core that crashes takes its own process down and is RECORDED as `crashed` — never mistaken for
      "this core has no options";
    * the loop is chunked and resumable — each core writes its own JSON, and a re-run SKIPS cores that
      already have one unless -Force;
    * progress is printed per core, so a long run is observable rather than a silent spinner.

  READ-ONLY against D:. It opens the DLLs and nothing else — no worker, task or coordinator is touched,
  and workers may be serving live rooms while this runs.

.PARAMETER CoresDir
  The deployed core directory. Default: the GL worker's assets/cores.

.PARAMETER OutDir
  Where the per-core extraction JSONs land (gitignored). Default: artifacts/core-options-extract.

.PARAMETER Force
  Re-extract cores that already have an extraction JSON.

.PARAMETER TimeoutSec
  Per-core watchdog. The child enforces it too; this is the outer belt-and-braces.

.PARAMETER ExtractOnly
  Stop after extraction (skip catalog + report generation).

.EXAMPLE
  pwsh -File scripts/extract-core-options.ps1
  pwsh -File scripts/extract-core-options.ps1 -Force
#>
[CmdletBinding()]
param(
    [string] $CoresDir   = 'D:\ArcadeStorage\worker-gl\assets\cores',
    [string] $OutDir,
    [string] $Catalog,
    [string] $Report,
    [switch] $Force,
    [int]    $TimeoutSec = 90,
    [switch] $ExtractOnly
)

$ErrorActionPreference = 'Stop'

# pwsh 7+ REQUIRED. Under Windows PowerShell 5.1 every child extraction is misclassified
# crashed-after-capture (0 ok / 42 needing attention) and CatalogBuilder then carries the old
# catalog blocks verbatim — a regen that silently changes nothing. Cost a night on 2026-08-08:
# the deployed cores had new options, the "regenerated" catalog didn't, and the drift check kept
# firing. Fail loudly instead.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "extract-core-options.ps1 requires PowerShell 7+ (pwsh). Under 5.1 all child extractions misclassify as crashed and the catalog silently keeps its old blocks. Re-run: pwsh -File scripts/extract-core-options.ps1"
}

$repo = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $PSScriptRoot 'extract-core-options'
if (-not $OutDir)  { $OutDir  = Join-Path $repo 'artifacts\core-options-extract' }
if (-not $Catalog) { $Catalog = Join-Path $repo 'src\MovieTheater\Arcade\core-options-catalog.json' }
if (-not $Report)  { $Report  = Join-Path $repo ("docs\arcade\core-options-drift-{0}.md" -f (Get-Date -Format 'yyyy-MM-dd')) }

$policy = Join-Path $tool 'policy.json'
$config = Join-Path $repo 'docker\arcade\config.worker-gl.yaml'

if (-not (Test-Path -LiteralPath $CoresDir)) { throw "cores dir not found: $CoresDir" }
if (-not (Test-Path -LiteralPath $config))   { throw "worker config not found: $config" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ── Build the harness once ──────────────────────────────────────────────────────────────────────────
Write-Host "Building the extractor..." -ForegroundColor Cyan
$buildLog = & dotnet build (Join-Path $tool 'ExtractCoreOptions.csproj') -c Release -v quiet --nologo 2>&1
if ($LASTEXITCODE -ne 0) { $buildLog | ForEach-Object { Write-Host $_ }; throw "dotnet build failed" }
$exe = Get-ChildItem -Path (Join-Path $tool 'bin\Release') -Filter 'extract-core-options.exe' -Recurse |
       Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw "extract-core-options.exe not found after build" }

# ── The cores to extract ────────────────────────────────────────────────────────────────────────────
# A real core is "<name>_libretro.dll" EXACTLY. That rule drops every backup shape in the deployed dir
# in one go: *.pre-*.dll (basename no longer ends in _libretro), *.bak, *.stock-prepatch, *.releasebak.
$dlls = Get-ChildItem -LiteralPath $CoresDir -Filter '*_libretro.dll' -File |
        Where-Object { $_.Extension -eq '.dll' -and [IO.Path]::GetFileNameWithoutExtension($_.Name).EndsWith('_libretro') } |
        Sort-Object Name

Write-Host ("Found {0} deployed cores in {1}" -f $dlls.Count, $CoresDir) -ForegroundColor Cyan

$tmp = Join-Path ([IO.Path]::GetTempPath()) 'arcade-core-option-harness'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

# Several deployed cores are MSYS2/UCRT64 builds importing libwinpthread-1.dll (mupen64plus_next,
# parallel_n64, kronos). The live worker is itself an MSYS2 build so it finds that DLL from its own
# environment; a bare LoadLibrary here does not, and the miss reports as win32 126 — which looks
# exactly like "unreadable core" unless you go looking. Hand the harness the toolchain bin dirs.
$depDirs = @(
    'C:\msys64\ucrt64\bin', 'C:\msys64\mingw64\bin'
) | Where-Object { Test-Path -LiteralPath $_ }
if ($depDirs.Count -eq 0) {
    Write-Warning "No MSYS2 bin dir found — MinGW-linked cores (mupen64plus_next, parallel_n64, kronos) will fail to load."
}
$depArg = ($depDirs -join ';')

$done = 0; $skipped = 0; $failed = 0; $i = 0
foreach ($dll in $dlls) {
    $i++
    $out = Join-Path $OutDir ([IO.Path]::GetFileNameWithoutExtension($dll.Name) + '.json')

    if ((Test-Path -LiteralPath $out) -and -not $Force) {
        $prev = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
        Write-Host ("[{0,2}/{1}] {2,-40} SKIP (have {3}, {4} options)" -f $i, $dlls.Count, $dll.Name, $prev.outcome, $prev.options.Count) -ForegroundColor DarkGray
        $skipped++
        continue
    }

    $cliArgs = @('extract', '--dll', $dll.FullName, '--out', $out, '--timeout', $TimeoutSec, '--tmp', $tmp)
    if ($depArg) { $cliArgs += @('--dep-dirs', $depArg) }
    $p = Start-Process -FilePath $exe -ArgumentList $cliArgs -NoNewWindow -PassThru -RedirectStandardError (Join-Path $tmp 'stderr.txt')
    if (-not $p.WaitForExit(($TimeoutSec + 30) * 1000)) {
        try { $p.Kill($true) } catch { try { $p.Kill() } catch { } }
        Write-Host ("[{0,2}/{1}] {2,-40} KILLED (outer watchdog)" -f $i, $dlls.Count, $dll.Name) -ForegroundColor Red
    }

    $outcome = 'crashed'; $count = 0
    if (Test-Path -LiteralPath $out) {
        $r = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
        $count = @($r.options).Count
        # A non-zero exit with data on disk means the core died AFTER handing us its option table (the
        # harness persists on capture, precisely so a late crash costs nothing). Record both facts.
        if ($p.ExitCode -ne 0 -and $r.outcome -notmatch 'timeout|load-failed|not-a-core') {
            $r.outcome = if ($count -gt 0) { 'crashed-after-capture' } else { 'crashed' }
            $r | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $out -Encoding utf8
        }
        $outcome = $r.outcome
    } else {
        # No file at all: the process died before it could even write its stub.
        @{ file = $dll.Name; outcome = 'crashed'; options = @(); notes = @("no output file; exit $($p.ExitCode)") } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $out -Encoding utf8
    }

    $colour = if ($outcome -like 'ok*') { 'Green' } elseif ($count -gt 0) { 'Yellow' } else { 'Red' }
    Write-Host ("[{0,2}/{1}] {2,-40} {3,-24} {4,3} options" -f $i, $dlls.Count, $dll.Name, $outcome, $count) -ForegroundColor $colour
    if ($outcome -like 'ok*') { $done++ } else { $failed++ }
}

Write-Host ""
Write-Host ("Extraction: {0} ok, {1} skipped (already had one), {2} needing attention. Output: {3}" -f $done, $skipped, $failed, $OutDir) -ForegroundColor Cyan

if ($ExtractOnly) { return }

# ── Fold into the catalog + write the drift report ──────────────────────────────────────────────────
Write-Host "Building catalog + drift report..." -ForegroundColor Cyan
& $exe build --extract-dir $OutDir --policy $policy --old $Catalog --config $config --out $Catalog --report $Report
if ($LASTEXITCODE -ne 0) { throw "catalog build failed ($LASTEXITCODE)" }

Write-Host ""
Write-Host "Catalog: $Catalog"
Write-Host "Report : $Report"
Write-Host "Now run: dotnet test src/MovieTheater.Tests" -ForegroundColor Yellow
