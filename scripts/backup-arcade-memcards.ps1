<#
.SYNOPSIS
    Snapshots the emulators' VIRTUAL MEMORY CARDS to a dated backup folder. Copy-only; never deletes a card.

.DESCRIPTION
    Save-states and libretro battery saves (.srm) are vaulted per user by the gateway's SaveStore. Virtual
    memory cards are vaulted per user by the WORKER (patches 0039 gc/ps2, 0041 dc/psp) into
    <CardVault>\<userId>\<system>\. This snapshots that vault: a dated copy, kept for -KeepDays.

    It matters because for several systems the card IS the progress — Gauntlet Dark Legacy keeps its
    named characters on the GameCube card, not in the save-state, and a PSP game's whole memstick save
    lives here — so losing the vault loses the progress no matter how healthy the save-state store is.
    Copy-only; it never deletes a card. New systems are picked up automatically (the whole vault is
    copied), so adding one to `emulator.cards` needs no change here.

    Register it with scripts/register-arcade-memcard-backup-task.ps1 (daily).

.PARAMETER WorkerDir  The GL worker's ConfDir (holds libretro/system + legacy_save).
.PARAMETER BackupDir  Root for the dated snapshots.
.PARAMETER KeepDays   Delete snapshot FOLDERS older than this. Only ever prunes this backup root.
#>
param(
    [string]$CardVault = "D:\ArcadeStorage\cards",
    [string]$BackupDir = "D:\ArcadeStorage\backup\memcards",
    [int]$KeepDays     = 30
)

$ErrorActionPreference = "Stop"

# Since patch 0039 the cards are VAULTED PER USER at <CardVault>\<userId>\<system>\ — the worker
# seeds the room owner's card in on boot and harvests it back on close. That vault is now the
# authoritative copy (the per-worker ConfDir dirs hold only whichever card is currently loaded), so
# it is what gets backed up. Cards still live outside the DB-backed save vault, hence this.
$sources = @($CardVault)

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
