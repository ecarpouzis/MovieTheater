<#
.SYNOPSIS
  Builds a reusable OS base (a C: drive folder) for arcade DOS games. Today: `win31` = Windows 3.1
  installed unattended, plus the S3 Trio 256-colour display driver and Sound Blaster + MPU-401 drivers.

.DESCRIPTION
  Output: <build root>\bases\<Base>\  — the root of a C: drive. build-dos-game.ps1 copies it and
  layers a game on top, so every Windows 3.1 game shares one base built once.

  Windows Setup runs for real inside DOSBox 0.74 (SETUP /H, unattended — no clicks), because Setup does
  far more than copy files (REG.DAT, Program Manager groups, a dozen INI files). The drivers Setup has
  no profile for are installed after it the way their own OEMSETUP.INF would, by expanding the files
  and writing the INI entries.

  Non-destructive: refuses to overwrite an existing base unless -Force.

.EXAMPLE
  ./build-dos-base.ps1 -WinSource D:\Arcade\build\dos\src\win31 -S3Source D:\Arcade\build\dos\src\s3-trio-1.70.04 -SbSource D:\Arcade\build\dos\src\sb-win31
#>
param(
    [ValidateSet('win31')][string]$Base = 'win31',
    [Parameter(Mandatory)][string]$WinSource,   # folder holding Windows 3.1's SETUP.EXE + the merged disk files
    [Parameter(Mandatory)][string]$S3Source,    # folder holding S3's Win 3.1 driver set (S3TRIO.DRV, OEMSETUP.INF)
    [Parameter(Mandatory)][string]$SbSource,    # Creative's "Sound Blaster drivers for Windows 3.1" (SBFM.DRV, MIDIMAP.CFG)
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DosBuild.psm1') -Force

$root = Get-DosBuildRoot
$out = Join-Path $root "bases\$Base"
if ((Test-Path $out) -and -not $Force) { throw "$out already exists — pass -Force to rebuild it" }
foreach ($f in "$WinSource\SETUP.EXE", "$S3Source\S3TRIO.DRV", "$SbSource\SBFM.DRV", "$SbSource\MIDIMAP.CFG") { if (-not (Test-Path $f)) { throw "missing $f" } }

$work = Join-Path $root "work\base-$Base"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$c = Join-Path $work 'c'
New-Item -ItemType Directory -Force $c | Out-Null

# --- 1. Windows Setup, unattended (SETUP.SHH format: see SETUP.SHH in the Windows source set) ---
Write-DosText "$c\ARCADE.SHH" @(
    '[sysinfo]', 'showsysinfo=no',
    '[configuration]', 'machine = ibm_compatible', 'display = vga', 'mouse = ps2mouse', 'network = nonet',
    'keyboard = t4s0enha', 'language = enu', 'kblayout = nodll',
    '[windir]', 'c:\windows',
    '[userinfo]', '"Arcade"', '"MovieTheater"',
    '[dontinstall]', 'readmes', 'games', 'screensavers', 'bitmaps',
    '[options]',
    '[printers]',
    '[endinstall]', 'configfiles = modify', 'endopt = exit')
Write-Host "[$Base] running Windows Setup in DOSBox (a window opens on the console; it closes itself)..."
Invoke-DosBox -WorkDir $work -Mounts @{ c = $c; e = $WinSource } -TimeoutSec 900 -Autoexec @(
    'c:', 'e:\setup.exe /h:c:\arcade.shh', 'exit')
Remove-Item "$c\ARCADE.SHH" -Force
$win = Join-Path $c 'WINDOWS'
$sys = Join-Path $win 'SYSTEM'
foreach ($f in "$win\WIN.COM", "$win\SYSTEM.INI", "$win\PROGMAN.EXE") {
    if (-not (Test-Path $f)) { throw "Windows Setup did not finish: $f is missing (see the DOSBox window / $work)" }
}
Write-Host "[$Base] Setup finished: $((Get-ChildItem $c -Recurse -File).Count) files"

# --- 2. S3 Trio 640x480 256 colours (OEMSETUP.INF profile D608 + its [D0608] work section) ---
Expand-MsFile "$S3Source\S3TRIO.DRV"   "$sys\S3TRIO.DRV"
Expand-MsFile "$S3Source\VDDS3764.386" "$sys\VDDS3764.386"
Expand-MsFile "$S3Source\VGA_ENG.3GR"  "$sys\VGA_ENG.3GR"
$ini = "$win\SYSTEM.INI"
Set-IniValue $ini 'boot' 'display.drv' 's3trio.drv'
Set-IniValue $ini 'boot' '386grabber' 'vga_eng.3gr'
Set-IniValue $ini 'boot.description' 'display.drv' 'S3 TrioV2 1.70.04  640x480  256'
Set-IniValue $ini '386Enh' 'display' 'vdds3764.386'
# polygon/ellipse OFF (the INF says on): DOSBox's S3 has no hardware polygon engine — with them on, the log
# fills with "XGA: Unhandled draw command 3" and those shapes are simply not drawn. Off = GDI draws them.
foreach ($kv in @('dac-type=nbt', 'polygon-support=off', 'ellipse-support=off', 'scache=on', 'textrmw=0',
                  'fastmmio=on', 'screen-size=640', 'color-format=8', 'dpi=96')) {
    $k, $v = $kv -split '=', 2
    Set-IniValue $ini 'DISPLAY' $k $v
}

# --- 3. Sound: Sound Blaster 2.0 wave + MPU-401 MIDI (DOSBox/DOSBox Pure defaults: A220 I7 D1, MPU 330) ---
Expand-MsFile "$WinSource\SNDBLST2.DR_" "$sys\SNDBLST2.DRV"
Expand-MsFile "$WinSource\VSBD.38_"    "$sys\VSBD.386"
# MIDI = Creative's SBFM.DRV driving the OPL at 388 (emulated inside DOSBox; no soundfont, no frontend
# MIDI, so the server and a local preview sound the same) — NOT Microsoft's msadlib.drv and NOT mpu401.drv.
# Both Microsoft drivers were tried under DOSBox Pure and measured (2026-09-30): mpu401.drv throws a
# "configuration or hardware problem" at boot (its port is DOSBox Pure's frontend-MIDI, which is off);
# msadlib.drv never registers a MIDI device — the sequencer answers "This device cannot play", the MIDI
# Mapper icon stays hidden, and Magic Theatre's songs are silent — while a raw OPL tone from DOS plays
# fine. SBFM.DRV on the same box played the game's own 3BLIND.MID at -20 dB. It ships with its own
# MIDIMAP.CFG whose CURRENT setup is "SB Basic FM" (channels 13-16 only → thin); we make "SB General FM"
# (all 16 channels → port "SB FM Synth") current by copying its record into the current-setup slot: the
# file is a header, a 54-byte current-setup record at 0x12, then the 54-byte setup table at 0x48.
Copy-Item "$SbSource\SBFM.DRV" "$sys\SBFM.DRV" -Force
$cfg = [IO.File]::ReadAllBytes("$SbSource\MIDIMAP.CFG")
$setups = [BitConverter]::ToUInt16($cfg, 0x10)
$general = -1
for ($i = 0; $i -lt $setups; $i++) { if ([Text.Encoding]::ASCII.GetString($cfg, 0x48 + 54 * $i, 16).TrimEnd([char]0) -eq 'SB General FM') { $general = $i } }
if ($general -lt 0) { throw "Creative MIDIMAP.CFG has no 'SB General FM' setup" }
[Array]::Copy($cfg, 0x48 + 54 * $general, $cfg, 0x12, 54)
[IO.File]::WriteAllBytes("$sys\MIDIMAP.CFG", $cfg)
Set-IniValue $ini 'drivers' 'Wave' 'sndblst2.drv'
Set-IniValue $ini 'drivers' 'MIDI' 'sbfm.drv'
Set-IniValue $ini 'sndblst.drv' 'port' '220'
Set-IniValue $ini 'sndblst.drv' 'int' '7'
Add-IniEntry $ini '386Enh' 'device' 'vsbd.386'

# --- 4. No swap file: DOSBox Pure keeps every C: write in the player's save overlay, and a 60 MB
#        WIN386.SWP would land there on the first boot. 16 MB of emulated RAM is plenty for 3.1 games.
Set-IniValue $ini '386Enh' 'Paging' 'No'
Set-IniValue $ini '386Enh' 'PagingFile' ''
Write-DosText "$c\AUTOEXEC.BAT" @('@ECHO OFF', 'PATH C:\WINDOWS;C:\', 'SET TEMP=C:\WINDOWS\TEMP')
New-Item -ItemType Directory -Force "$win\TEMP" | Out-Null

# --- 5. Mouse: NO pointer acceleration. The arcade shim positions Windows' cursor ABSOLUTELY by sending
#        (target - model) deltas through a known gain; acceleration makes the gain depend on speed, and the
#        cursor would land short of slow moves and past fast ones. 0 = one mickey moves one fixed step.
$winIni = "$win\WIN.INI"
Set-IniValue $winIni 'windows' 'MouseSpeed' '0'
Set-IniValue $winIni 'windows' 'MouseThreshold1' '0'
Set-IniValue $winIni 'windows' 'MouseThreshold2' '0'

# --- 6. Publish ---
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
Move-Item $c $out
Write-Host "[$Base] base ready: $out"
