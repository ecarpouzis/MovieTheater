<#
.SYNOPSIS
  Builds one arcade DOS/Win3.x game from a recipe: OS base + the game's install, packaged as a single
  .zip that DOSBox Pure boots straight into the game.

.DESCRIPTION
  Recipe (recipes\*.json):
    name    — output file name (= the arcade's game key) and display title
    base    — "win31" (bases\win31 from build-dos-base.ps1) or "none" (bare DOS, empty C:)
    source  — folder under <build root>\src holding the extracted disc
    steps   — install steps, applied in order, paths relative to source (from) / C: (to):
                is3z      { from, to }               extract an InstallShield 3 library (is3z_extract.py)
                copyDir   { from, to, exclude[] }    copy a folder tree
                copyFiles { from, to, files[] }      copy named files
                ini       { file, section, values{} } write INI keys (Windows 3.x format)
    launch  — { dir, program }: Windows games become the Windows SHELL (quitting the game quits
              Windows and DOSBOX.BAT starts it again); DOS games are run from DOSBOX.BAT.

  DOSBOX.BAT at the zip root is DOSBox Pure's auto-start. Installs are replayed as file operations
  (what the vendor's installer would do), never by driving the installer's GUI.

  Output: <build root>\out\<name>.zip. Non-destructive: refuses to overwrite unless -Force.

.EXAMPLE
  ./build-dos-game.ps1 -Recipe ./recipes/magic-theatre.json
#>
param([Parameter(Mandatory)][string]$Recipe, [switch]$Force)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DosBuild.psm1') -Force

$r = Get-Content $Recipe -Raw | ConvertFrom-Json
$root = Get-DosBuildRoot
$src = Join-Path $root "src\$($r.source)"
$outZip = Join-Path $root "out\$($r.name).zip"
if ((Test-Path $outZip) -and -not $Force) { throw "$outZip already exists — pass -Force to rebuild it" }
if (-not (Test-Path $src)) { throw "missing source folder $src" }

$slug = ($r.name -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLower()
$work = Join-Path $root "work\game-$slug"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$c = Join-Path $work 'c'
New-Item -ItemType Directory -Force $c | Out-Null

$windows = $r.base -ne 'none'
if ($windows) {
    $base = Join-Path $root "bases\$($r.base)"
    if (-not (Test-Path "$base\WINDOWS\WIN.COM")) { throw "base '$($r.base)' not built — run build-dos-base.ps1" }
    & robocopy $base $c /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy of the base failed ($LASTEXITCODE)" }
}

function Resolve-In([string]$dir, [string]$rel) { Join-Path $dir ($rel -replace '/', '\') }

foreach ($s in $r.steps) {
    switch ($s.op) {
        'is3z' {
            & python (Join-Path $PSScriptRoot 'is3z_extract.py') (Resolve-In $src $s.from) (Resolve-In $c $s.to)
            if ($LASTEXITCODE) { throw "is3z extract failed: $($s.from)" }
        }
        'copyDir' {
            $xf = @(); if ($s.exclude) { $xf = @('/XF') + $s.exclude }
            & robocopy (Resolve-In $src $s.from) (Resolve-In $c $s.to) /E /NFL /NDL /NJH /NJS /NP @xf | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "copyDir failed: $($s.from) ($LASTEXITCODE)" }
        }
        'copyFiles' {
            $to = Resolve-In $c $s.to
            New-Item -ItemType Directory -Force $to | Out-Null
            foreach ($f in $s.files) { Copy-Item (Resolve-In (Resolve-In $src $s.from) $f) (Join-Path $to $f) -Force }
        }
        'ini' {
            $file = Resolve-In $c $s.file
            foreach ($p in $s.values.PSObject.Properties) { Set-IniValue $file $s.section $p.Name $p.Value }
        }
        default { throw "unknown step op '$($s.op)'" }
    }
    Write-Host "  step $($s.op) $($s.from)$($s.file) -> ok"
}

# Launch. DOS file names are 8.3 and upper-case, so every path written here is too.
$dir = $r.launch.dir.ToUpper()
$prog = $r.launch.program.ToUpper()
if (-not (Test-Path (Join-Path $c ($dir.Substring(3) + "\$prog")))) { throw "launch target $dir\$prog is not on C:" }
$bat = @('@ECHO OFF', 'PATH C:\WINDOWS;C:\', 'SET TEMP=C:\WINDOWS\TEMP', 'SET BLASTER=A220 I7 D1 H5 T6', 'C:', "CD $dir")
if ($windows) {
    Set-IniValue "$c\WINDOWS\SYSTEM.INI" 'boot' 'shell' "$dir\$prog"
    $bat += @(':AGAIN', 'C:\WINDOWS\WIN.COM', 'GOTO AGAIN')
} else {
    $bat += @(':AGAIN', $prog, 'GOTO AGAIN')
}
Write-DosText "$c\DOSBOX.BAT" $bat

New-Item -ItemType Directory -Force (Split-Path $outZip) | Out-Null
if (Test-Path $outZip) { Remove-Item $outZip -Force }
& $SevenZip a -tzip -mx=5 -bd $outZip "$c\*" | Out-Null
if ($LASTEXITCODE) { throw "7z failed ($LASTEXITCODE)" }
$n = (Get-ChildItem $c -Recurse -File).Count
Write-Host ("[{0}] {1} files -> {2} ({3:N1} MB)" -f $r.name, $n, $outZip, ((Get-Item $outZip).Length / 1MB))
