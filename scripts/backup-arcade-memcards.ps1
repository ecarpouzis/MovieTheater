<#
.SYNOPSIS
    Snapshots the emulators' VIRTUAL MEMORY CARDS to a dated backup folder. Copy-only; never deletes a card.

.DESCRIPTION
    Save-states and libretro battery saves (.srm) are vaulted per user by the gateway's SaveStore. Virtual
    memory cards are NOT: the disc-era cores write them straight into the worker's own system dir, so each
    is a SINGLE GLOBAL FILE shared by every player, with no vault copy and no backup. That is where real
    in-game progress lives for those systems — Gauntlet Dark Legacy keeps its named characters on the
    GameCube card, not in the save-state — so losing that directory loses the characters no matter how
    healthy the save vault is.

    Per-user memory-card vaulting is the real fix (arcade task: "Vault virtual memory cards per user").
    Until it lands, this is the cheap insurance: a dated copy, kept for -KeepDays.

    Register it with scripts/register-arcade-memcard-backup-task.ps1 (daily).

.PARAMETER WorkerDir  The GL worker's ConfDir (holds libretro/system + legacy_save).
.PARAMETER BackupDir  Root for the dated snapshots.
.PARAMETER KeepDays   Delete snapshot FOLDERS older than this. Only ever prunes this backup root.
#>
param(
    [string]$WorkerDir = "D:\ArcadeStorage\worker-gl",
    [string]$BackupDir = "D:\ArcadeStorage\backup\memcards",
    [int]$KeepDays     = 30
)

$ErrorActionPreference = "Stop"

# Where each core keeps its card. Add a line when a new memory-card system goes live.
$sources = @(
    # Dolphin: a DIRECTORY of .gci files per region (GameCube). This is Gauntlet's characters.
    (Join-Path $WorkerDir "libretro\legacy_save\User\GC"),
    # PCSX2: Mcd001.ps2 / Mcd002.ps2 (8 MB card images).
    (Join-Path $WorkerDir "libretro\system\pcsx2\memcards")
)

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$dest  = Join-Path $BackupDir $stamp
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$copied = 0
foreach ($src in $sources) {
    if (-not (Test-Path $src)) { Write-Host "skip (absent): $src"; continue }
    $leaf = Split-Path $src -Leaf
    $to   = Join-Path $dest $leaf
    Copy-Item -Path $src -Destination $to -Recurse -Force
    $n = @(Get-ChildItem $to -Recurse -File).Count
    $copied += $n
    Write-Host "backed up $n file(s): $src"
}

if ($copied -eq 0) {
    # Nothing to keep — don't leave an empty dated folder behind implying a good backup.
    Remove-Item $dest -Recurse -Force
    Write-Host "no memory cards found; nothing backed up"
    return
}
Write-Host "snapshot: $dest ($copied file(s))"

# Prune old SNAPSHOTS only — never the live cards. Scoped to $BackupDir, dated names only.
$cutoff = (Get-Date).AddDays(-$KeepDays)
Get-ChildItem $BackupDir -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d{8}-\d{6}$' -and $_.CreationTime -lt $cutoff } |
    ForEach-Object { Write-Host "pruning old snapshot: $($_.Name)"; Remove-Item $_.FullName -Recurse -Force }
