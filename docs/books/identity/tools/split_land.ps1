# Land the split lane's decisions (TOOLS_TODO 27): P-NNN.jsonl files -> books-series-split -> the resolve and
# every standing check. Writes books.db. Run it yourself, at the console, with the batches named.
#
#   pwsh docs/books/identity/tools/split_land.ps1 -Wave 14 P-001 P-002 ...
#
# Order, and why:
#   1. check_splits --all            the named files AND every other P- file (one item, one split; no two spellings
#                                    of one canonical key across files)
#   2. project each file             books-series-split reads {itemId, key} only; a P- file's shelf lines would be
#                                    counted as bad lines, so the verb is fed the projection, never the P- file
#   3. DRY RUN of the verb per file  it must report 0 "not found" and 0 "bad line(s)" — the verb does not exit
#                                    non-zero on either, so this script reads its summary line and stops on them
#   4. backup_live                   the only way back if anything below goes wrong
#   5. books-series-split --apply    per file, each with its OWN --undo-log CSV (the walk-back record: ItemId,
#                                    PreviousParsedSeriesKey, NewParsedSeriesKey, SeriesIdAtSplit)
#   6. wave_fix.ps1 -Resolve         takes a fresh snapshot, runs books-resolve --series (which builds the new
#                                    Series from the new keys), reseat_flags, merge_refusals, pass2, the chain
#                                    (reading-order + containment) and every PLAN §8 check
#   7. check_splits --landed         which shelf each new key became -> a sheet next_batch.py --revisit-file reads,
#                                    so the new shelves are read into the identity pass with their `run` ids
#
# TOOLS_TODO 27 (d) lists `books-resolve --series` as its own step BEFORE wave_fix. It is deliberately not run
# here: wave_fix -Resolve runs the resolve itself, AFTER taking the merge_refusals snapshot, and that snapshot is
# only meaningful while the old shelf ids still hold their items. A resolve run first would leave merge_refusals
# measuring nothing (the collected editions would already have moved), so the lane would land with no
# containment refusals for them — the wave-2 failure mode. The split alone moves no item (it rewrites
# ParsedSeriesKey; Item.SeriesId changes only at the resolve), so the snapshot wave_fix takes is still "before".
#
# Walk-back: `python tools/check_splits.py --walkback <the CSV> --out back.jsonl`, then
# `books-series-split --in back.jsonl --apply` and `wave_fix.ps1 -Resolve`. The CSV paths are printed at the end.

param(
    [Parameter(Mandatory = $true)][string]$Wave,
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$Batches
)
$ErrorActionPreference = "Stop"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$repo = "F:/Work/MovieTheater"
$tools = "$repo/docs/books/identity/tools"
$ctools = "$repo/docs/books/containment/tools"
$undo = "$repo/docs/books/identity/undo"
$db = "$repo/data/books/v2/books.db"
$exe = "$repo/src/MovieTheater.BooksHost/bin/Debug/net10.0/MovieTheater.BooksHost.exe"
Set-Location $repo

function Step([string]$label, [scriptblock]$body) {
    Write-Host ""; Write-Host ("=" * 96); Write-Host "== $label"; Write-Host ("=" * 96)
    & $body
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "STOP: '$label' exited $LASTEXITCODE. The split lane is halted here." -ForegroundColor Red
        Write-Host "      Nothing further runs. If the DB was already written, walk back with the CSVs listed below"
        Write-Host "      or restore the backup printed above."
        foreach ($c in $script:csvs) { Write-Host "      walk-back CSV: $c" }
        exit $LASTEXITCODE
    }
}

$bad = $Batches | Where-Object { $_ -notmatch '^P-\d{3,}$' }
if ($bad) { Write-Host "STOP: not a split batch name: $($bad -join ', ') (expected P-NNN)" -ForegroundColor Red; exit 2 }
$files = $Batches | ForEach-Object { "$repo/docs/books/identity/decisions/$_.jsonl" }
$missing = $files | Where-Object { -not (Test-Path $_) }
if ($missing) { Write-Host "STOP: no split file for: $($missing -join ', ')" -ForegroundColor Red; exit 2 }
if (-not (Test-Path $exe)) { Write-Host "STOP: BooksHost exe not built at $exe" -ForegroundColor Red; exit 2 }
New-Item -ItemType Directory -Force $undo | Out-Null
$script:csvs = @()
Write-Host "split lane wave ${Wave}: $($Batches -join ', ')"

Step "check_splits --all (the named files, and every P- file against them)" { python "$tools/check_splits.py" @Batches --all }

$verbIn = @{}
foreach ($b in $Batches) {
    $verbIn[$b] = "$undo/split-$b-$stamp.verb.jsonl"
    Step "project $b -> {itemId, key}" { python "$tools/check_splits.py" --project $b --out $verbIn[$b] }
}

foreach ($b in $Batches) {
    Step "books-series-split DRY RUN $b (must find every item and read every line)" {
        & $exe books-series-split --in $verbIn[$b] --db $db | Tee-Object -Variable lines | Out-Host
        if ($LASTEXITCODE -ne 0) { return }
        $done = $lines | Where-Object { "$_" -match '^done:' } | Select-Object -Last 1
        if (-not $done) { Write-Host "no summary line from the verb" -ForegroundColor Red; $global:LASTEXITCODE = 3; return }
        if ($done -notmatch ' 0 item\(s\) not found, 0 bad line\(s\)') {
            Write-Host "the dry run reports missing items or bad lines: $done" -ForegroundColor Red
            $global:LASTEXITCODE = 3; return
        }
    }
}

Step "backup_live (SQLite online backup of books.db + books-legs.db)" { python "$ctools/backup_live.py" }

foreach ($b in $Batches) {
    $csv = "$undo/split-$b-$stamp.csv"
    $script:csvs += $csv
    Step "books-series-split --apply $b (walk-back CSV: $csv)" {
        & $exe books-series-split --in $verbIn[$b] --db $db --undo-log $csv --apply }
}

Step "wave_fix.ps1 -Resolve -Wave $Wave (snapshot -> books-resolve --series -> reseat -> refusals -> chain -> checks)" {
    pwsh "$tools/wave_fix.ps1" -Resolve -Wave $Wave }

$sheet = "$undo/split-wave$Wave-$stamp-newshelves.tsv"
Step "check_splits --landed (which shelf each new key became; the sheet feeds next_batch.py --revisit-file)" {
    python "$tools/check_splits.py" --landed @Batches --out $sheet }

Write-Host ""
Write-Host "SPLIT LANE LANDED: $($Batches -join ', ')" -ForegroundColor Green
Write-Host "walk-back CSVs (ItemId, PreviousParsedSeriesKey, NewParsedSeriesKey, SeriesIdAtSplit):"
foreach ($c in $script:csvs) { Write-Host "   $c" }
Write-Host "new shelves: $sheet  ->  python tools/next_batch.py --revisit-file $sheet --note `"split lane wave $Wave`""
