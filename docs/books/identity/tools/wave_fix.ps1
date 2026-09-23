# Finish a wave that wave_land.ps1 halted at a containment check because an identity MERGE moved collected
# editions onto a judged shelf. The fix is refusals written into the containment decision files (done by hand,
# by reading, before this runs); this script re-imports the curated spans, rebuilds the chain, and re-runs every
# standing check. It writes to books.db — run it yourself, at the console, after check_decisions.py is green.
#
#   pwsh docs/books/identity/tools/wave_fix.ps1
#   pwsh docs/books/identity/tools/wave_fix.ps1 -Resolve -Wave 2
#
# -Resolve inserts the steps a REBUILD needs, in the only order that works: a fresh snapshot (the old shelf
# ids stop existing the moment the resolve runs), the resolve itself, then reseat_flags (a merge or split
# carries a ContainmentFlag's ITEM to another shelf and leaves its SeriesId behind — four of wave 2's
# audit_identity failures were exactly that) and merge_refusals (the coverage contract for every collected
# edition the rebuild moved). Only then check_decisions, which those two steps exist to make true.
param([switch]$Resolve, [string]$Wave = "2")
$ErrorActionPreference = "Stop"
$repo = "F:/Work/MovieTheater"
$tools = "$repo/docs/books/identity/tools"
$ctools = "$repo/docs/books/containment/tools"
$db = "$repo/data/books/v2/books.db"
$exe = "$repo/src/MovieTheater.BooksHost/bin/Debug/net10.0/MovieTheater.BooksHost.exe"
Set-Location $repo

function Step([string]$label, [scriptblock]$body) {
    Write-Host ""; Write-Host ("=" * 96); Write-Host "== $label"; Write-Host ("=" * 96)
    & $body
    if ($LASTEXITCODE -ne 0) { Write-Host "STOP: '$label' exited $LASTEXITCODE." -ForegroundColor Red; exit $LASTEXITCODE }
}

$legs = "$repo/data/books/v2/books-legs.db"
if ($Resolve) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $snap = "$repo/docs/books/identity/undo/wavefix-$stamp-before.json"
    Step "backup_live (before any write)" { python "$ctools/backup_live.py" }
    Step "merge_refusals --snapshot (every collected edition -> its shelf, while the old ids still exist)" {
        python "$tools/merge_refusals.py" --snapshot $snap }
    Step "books-resolve --series (splits the wrongly-fused runs back out)" {
        & $exe books-resolve --series --db $db --legs $legs }
    Step "reseat_flags --apply (a flag follows its ITEM, never the shelf it used to be on)" {
        python "$tools/reseat_flags.py" --apply }
    Step "merge_refusals --apply (refusals for every edition the rebuild moved)" {
        python "$tools/merge_refusals.py" --from $snap --wave $Wave --apply }
}
# pass2 ALWAYS runs (2026-09-11): wave 3's resume skipped it, imported the previous wave's stale pass2_spans.jsonl,
# and the refusals merge_refusals had just written never reached the DB — 16 CV-derived spans nested files unjudged.
# A rebuild moves items; the ORIGIN containment file keeps its old line and pass2 fails on "decision(s) for items
# not in this series" (waves 18 and 20 halted here). retire_moved_lines comments a line out ONLY when the item's
# new shelf has a file that decides it, so nothing is ever left undecided — safe to run on every wave.
Step "retire_moved_lines --apply (origin lines for items a rebuild moved; destination file must decide them)" { python "$ctools/retire_moved_lines.py" --apply }
Step "pass2 (re-expand the decision files, including the refusals just written)" { python "$ctools/pass2.py" }
Step "check_decisions (every containment decision file must pass)" { python "$ctools/check_decisions.py" }
Step "books-curated-spans-import --apply (refusals retract the pass's own rows; gold is never touched)" {
    & $exe books-curated-spans-import --in "$ctools/pass2_spans.jsonl" --db $db --batch wave1-fix --apply }
Step "run_chain3.ps1 (collected-editions -> reading-order -> containment)" { pwsh "$repo/run_chain3.ps1" }
Step "SeriesTitle.RunCount recount (one row went stale when a merge deleted a run)" {
    python -c "import sqlite3; c=sqlite3.connect('$db'); n=c.execute('UPDATE SeriesTitle SET RunCount=(SELECT count(*) FROM Series s WHERE s.TitleId=SeriesTitle.Id) WHERE RunCount<>(SELECT count(*) FROM Series s WHERE s.TitleId=SeriesTitle.Id)').rowcount; c.commit(); print('RunCount rows corrected:', n)" }

Step "SeriesTitle prune (a merge deleted the last run of a title; nothing but Series.TitleId references it)" {
    python -c "import sqlite3; c=sqlite3.connect('$db'); n=c.execute('DELETE FROM SeriesTitle WHERE NOT EXISTS (SELECT 1 FROM Series s WHERE s.TitleId=SeriesTitle.Id)').rowcount; c.commit(); print('orphan titles pruned:', n)" }
Step "audit_containment" { python "$ctools/audit_containment.py" }
Step "overclaim_check" { python "$ctools/overclaim_check.py" }
Step "overlap_check --all" { python "$ctools/overlap_check.py" --all }
Step "check_decisions" { python "$ctools/check_decisions.py" }
Step "audit_issue_details" { python "$ctools/audit_issue_details.py" }
Step "armed_unjudged_check" { python "$ctools/armed_unjudged_check.py" }
Step "coverage_ledger" { python "$ctools/coverage_ledger.py" }
Step "audit_identity" { python "$tools/audit_identity.py" }
Step "identity_coverage" { python "$tools/identity_coverage.py" }
Step "Saga S14966 (compare by hand against the pre-wave snapshot)" { python "$ctools/runsql.py" $db "$ctools/sql/series_report.sql" ":sid=14966" }
Write-Host ""; Write-Host "WAVE FIX COMPLETE" -ForegroundColor Green
