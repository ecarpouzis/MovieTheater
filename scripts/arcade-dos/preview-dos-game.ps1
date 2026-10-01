<#
.SYNOPSIS
  Runs a built DOS/Win3.x game zip in the SAME core the arcade uses (DOSBox Pure, copied from the GL
  worker) inside a private RetroArch config, optionally recording its audio so sound can be MEASURED
  instead of listened to.

.DESCRIPTION
  -Seconds N : quit RetroArch after N seconds (otherwise it stays open for a human).
  -Record    : route RetroArch's audio into VB-CABLE, capture CABLE Output with ffmpeg for the run,
               write <preview>\rec-<stamp>.wav and print the RMS level per 5-second window
               (-inf / below -80 dB = silence). While recording, nothing plays on the speakers.
               ⚠ VB-CABLE belongs to the arcade CAPTURE lane; refuses to run if any room is live.
               (RetroArch's own --record is useless here: DOSBox Pure renders through GL, so it must
               record post-shader, and it ENDS the recording on every video-mode change — the DOS
               text screen -> Windows 640x480 switch kills it in the first seconds.)
  -Fresh     : delete the game's .pure.zip save overlay first (DOSBox Pure persists every C: write).
  Log: <preview>\retroarch.log (the core's own [DOSBOX] lines included).
#>
param([Parameter(Mandatory)][string]$Zip, [int]$Seconds = 0, [switch]$Record, [switch]$Fresh)
$ErrorActionPreference = 'Stop'
$p = 'D:\Arcade\build\dos\preview'
$ra = 'E:\LaunchBox\Emulators\RetroArch\retroarch.exe'
$ffmpeg = 'C:\Program Files\Jellyfin\Server\ffmpeg.exe'
if ($Record -and $Seconds -le 0) { throw '-Record needs -Seconds' }
if ($Record) {
    $live = (curl.exe -s localhost:8000/status | ConvertFrom-Json) | Where-Object { $_.room }
    if ($live) { throw "arcade room(s) live ($($live.room -join ', ')) — VB-CABLE may be in use; not recording" }
}
New-Item -ItemType Directory -Force "$p\system", "$p\saves", "$p\states" | Out-Null
Get-Process retroarch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
Copy-Item 'D:\ArcadeStorage\worker-gl\assets\cores\dosbox_pure_libretro.dll' "$p\dosbox_pure_libretro.dll" -Force
$cfg = @(
    "system_directory = `"$p\system`"", "savefile_directory = `"$p\saves`"", "savestate_directory = `"$p\states`"",
    "core_options_path = `"$p\core-options.cfg`"", 'video_fullscreen = "false"', 'pause_nonactive = "false"',
    'log_verbosity = "true"', 'libretro_log_level = "1"', 'config_save_on_exit = "false"', 'menu_driver = "rgui"',
    # Game focus ON: the keyboard goes to DOS/Windows and RetroArch hotkeys are off. Without it SPACE is
    # fast-forward (which also MUTES) — a stray key press during a test read as "the sound died".
    'input_auto_game_focus = "1"',
    # No RetroPad on port 1: RetroArch's default keyboard->RetroPad binds (S = X, ...) made a key press ALSO
    # press a pad button, which DOSBox Pure maps to keys of its own (S typed "s "). Keyboard only.
    'input_libretro_device_p1 = "0"')
if ($Record) { $cfg += @('audio_driver = "wasapi"', 'audio_device = "CABLE Input (VB-Audio Virtual Cable)"') }
Set-Content "$p\retroarch.cfg" -Encoding ascii -Value $cfg
if ($Fresh) { Remove-Item "$p\saves\$([IO.Path]::GetFileNameWithoutExtension($Zip)).pure.zip" -ErrorAction SilentlyContinue }
Remove-Item "$p\retroarch.log" -ErrorAction SilentlyContinue

$rec = $null; $ff = $null
if ($Record) {
    $rec = "$p\rec-$(Get-Date -Format yyyyMMdd-HHmmss).wav"
    $ff = Start-Process $ffmpeg -ArgumentList @('-hide_banner', '-loglevel', 'error', '-f', 'dshow', '-i',
        'audio="CABLE Output (VB-Audio Virtual Cable)"', '-t', ($Seconds + 2), '-ac', '2', '-ar', '48000', "`"$rec`"") -PassThru -WindowStyle Hidden
}
$raArgs = @('--config', "`"$p\retroarch.cfg`"", '-L', "`"$p\dosbox_pure_libretro.dll`"", '--verbose', '--log-file', "`"$p\retroarch.log`"", "`"$Zip`"")
$proc = Start-Process $ra -ArgumentList $raArgs -WorkingDirectory $p -PassThru
if ($Seconds -le 0) { "RetroArch running (pid $($proc.Id)); log: $p\retroarch.log"; return }
Start-Sleep $Seconds
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit(15000)) { $proc | Stop-Process -Force }
if ($ff) {
    if (-not $ff.WaitForExit(20000)) { $ff | Stop-Process -Force }
    if (-not (Test-Path $rec)) { throw "ffmpeg wrote no recording ($rec)" }
    "recording: $rec"
    & $ffmpeg -hide_banner -nostats -i $rec -af 'asetnsamples=n=240000,astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level' -f null - 2>&1 |
        Select-String 'pts_time|RMS_level' | ForEach-Object { $_.Line.Trim() } |
        ForEach-Object -Begin { $t = '' } -Process { if ($_ -match 'pts_time:([\d.]+)') { $t = [double]$Matches[1] } elseif ($_ -match 'RMS_level=(\S+)') { '{0,6:N0}s  RMS {1} dB' -f $t, $Matches[1] } }
}
