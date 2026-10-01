# Shared helpers for the arcade DOS/Win3.x build pipeline (build-dos-base.ps1, build-dos-game.ps1).
# Sources (OS install sets, game discs) never live in the repo — they sit under the build root on D:.

# A module does NOT inherit the calling script's $ErrorActionPreference. Without this, a throwing method
# call inside a loop condition is only statement-terminating and the loop spins forever (it did).
$ErrorActionPreference = 'Stop'

$script:SevenZip = 'C:\Program Files\7-Zip\7z.exe'
$script:DosBoxExe = 'E:\LaunchBox\DOSBox\DOSBox.exe'   # DOSBox 0.74 — only used at BUILD time to run installers

function Get-DosBuildRoot { 'D:\Arcade\build\dos' }

# INI edits that keep Windows 3.x's format: ANSI, CRLF, sections created on demand, keys replaced in place.
function Set-IniValue {
    param([string]$Path, [string]$Section, [string]$Key, [AllowEmptyString()][string]$Value)
    $enc = [Text.Encoding]::GetEncoding(1252)
    # Typed assignment: `$lines = if (...) { [List]... }` would ENUMERATE the list into a fixed-size array.
    [Collections.Generic.List[string]]$lines = [Collections.Generic.List[string]]::new()
    if (Test-Path $Path) { $lines.AddRange([string[]]([IO.File]::ReadAllText($Path, $enc) -split "`r?`n")) }
    while ($lines.Count -and $lines[$lines.Count - 1] -eq '') { $lines.RemoveAt($lines.Count - 1) }
    $secIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -ieq "[$Section]") { $secIdx = $i; break } }
    if ($secIdx -lt 0) {
        if ($lines.Count) { $lines.Add('') }
        $lines.Add("[$Section]"); $lines.Add("$Key=$Value")
    } else {
        $end = $lines.Count
        for ($i = $secIdx + 1; $i -lt $lines.Count; $i++) { if ($lines[$i].TrimStart().StartsWith('[')) { $end = $i; break } }
        $hit = -1
        for ($i = $secIdx + 1; $i -lt $end; $i++) { if ($lines[$i] -match "^\s*$([regex]::Escape($Key))\s*=") { $hit = $i; break } }
        if ($hit -ge 0) { $lines[$hit] = "$Key=$Value" }
        else {
            $ins = $end
            while ($ins -gt $secIdx + 1 -and $lines[$ins - 1].Trim() -eq '') { $ins-- }
            $lines.Insert($ins, "$Key=$Value")
        }
    }
    [IO.File]::WriteAllText($Path, (($lines -join "`r`n") + "`r`n"), $enc)
}

# For MULTI-valued keys ([386Enh] device=, one line per VxD): append the line unless that exact
# key=value is already there. Set-IniValue would REPLACE the first device= line — i.e. drop a VxD.
function Add-IniEntry {
    param([string]$Path, [string]$Section, [string]$Key, [string]$Value)
    $enc = [Text.Encoding]::GetEncoding(1252)
    [Collections.Generic.List[string]]$lines = [Collections.Generic.List[string]]::new()
    $lines.AddRange([string[]]([IO.File]::ReadAllText($Path, $enc) -split "`r?`n"))
    $secIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -ieq "[$Section]") { $secIdx = $i; break } }
    if ($secIdx -lt 0) { throw "[$Section] not found in $Path" }
    $end = $lines.Count
    for ($i = $secIdx + 1; $i -lt $lines.Count; $i++) { if ($lines[$i].TrimStart().StartsWith('[')) { $end = $i; break } }
    for ($i = $secIdx + 1; $i -lt $end; $i++) { if ($lines[$i].Trim() -ieq "$Key=$Value") { return } }
    $ins = $end
    while ($ins -gt $secIdx + 1 -and $lines[$ins - 1].Trim() -eq '') { $ins-- }
    $lines.Insert($ins, "$Key=$Value")
    while ($lines.Count -and $lines[$lines.Count - 1] -eq '') { $lines.RemoveAt($lines.Count - 1) }
    [IO.File]::WriteAllText($Path, (($lines -join "`r`n") + "`r`n"), $enc)
}

# Expand a Microsoft-compressed (SZDD, trailing '_') file — or copy a plain one — to an exact target name.
function Expand-MsFile {
    param([string]$Source, [string]$Destination)
    New-Item -ItemType Directory -Force (Split-Path $Destination) | Out-Null
    if ($Source.EndsWith('_')) {
        & "$env:SystemRoot\System32\expand.exe" $Source $Destination | Out-Null
        if (-not (Test-Path $Destination)) { throw "expand failed: $Source" }
    } else { Copy-Item $Source $Destination -Force }
}

function Write-DosText {
    param([string]$Path, [string[]]$Lines)
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    [IO.File]::WriteAllText($Path, (($Lines -join "`r`n") + "`r`n"), [Text.Encoding]::GetEncoding(1252))
}

# Runs DOSBox 0.74 headlessly-ish (it still opens a window on the console session) with a generated
# config whose [autoexec] is $Autoexec, and waits for it to exit. The autoexec MUST end with `exit`.
function Invoke-DosBox {
    param([string]$WorkDir, [hashtable]$Mounts, [string[]]$Autoexec, [int]$TimeoutSec = 900, [string]$Machine = 'svga_s3')
    $conf = Join-Path $WorkDir 'build-dosbox.conf'
    $mountLines = foreach ($k in ($Mounts.Keys | Sort-Object)) { "mount $k `"$($Mounts[$k])`"" }
    Write-DosText $conf (@(
        '[sdl]', 'output=surface', 'windowresolution=original',
        '[dosbox]', "machine=$Machine", 'memsize=16',
        '[cpu]', 'core=dynamic', 'cputype=auto', 'cycles=max',
        '[mixer]', 'nosound=true',
        '[autoexec]') + $mountLines + $Autoexec)
    $p = Start-Process -FilePath $script:DosBoxExe -ArgumentList @('-conf', "`"$conf`"", '-noconsole') -WorkingDirectory $WorkDir -PassThru
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        throw "DOSBox did not exit within $TimeoutSec s (pid $($p.Id) left running so its window can be inspected)"
    }
}

Export-ModuleMember -Function Get-DosBuildRoot, Set-IniValue, Add-IniEntry, Expand-MsFile, Write-DosText, Invoke-DosBox -Variable SevenZip, DosBoxExe
