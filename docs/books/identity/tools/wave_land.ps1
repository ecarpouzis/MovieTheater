# Land one wave of identity decisions. PLAN §12's recipe, one script, every step printed.
#
#   pwsh docs/books/identity/tools/wave_land.ps1 -Wave 2 A-001 A-002 A-003 ...
#
# Order (TOOLS_TODO 9, learned from wave 1): check -> backup -> apply -> SNAPSHOT -> resolve ->
# merge_refusals -> check_decisions -> pass2 -> curated-spans-import -> chain3 -> the §8 checks. The
# containment repair sits AFTER the resolve because the merge it repairs does not exist until then, and
# the snapshot sits BEFORE it because the old shelf ids stop existing when it runs.
#
# Every step stops the wave on a non-zero exit, because the whole point of the §8 block is that a non-zero
# is a STOP: the dev connection IS production (§5.1), `books-resolve --series` MERGES shelves that share a
# cv: key and RENAMES linked ones (§6.5, §6.6), and the backup taken in step 1 is the only way back.
#
# Run it yourself, at the console, with the batches named. It is deliberately not callable without them.
#
# Any batch KIND may be named: a tier batch (A-/B-/C-/D-), a revisit (R-) or an ITEM batch (X-), which decides
# BOOKS rather than shelves (TOOLS_TODO 16) and whose lines are only I / C / N. The steps are the same for all
# three -- an X- batch simply lands no SeriesKeyLink rows and merges nothing -- so the only thing this script
# has to do about them is refuse a name whose decision file is not there, before the backup is taken.

param(
    [Parameter(Mandatory = $true)][string]$Wave,
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$Batches
)
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

$ErrorActionPreference = "Stop"
$repo = "F:/Work/MovieTheater"
$tools = "$repo/docs/books/identity/tools"
$ctools = "$repo/docs/books/containment/tools"
$db = "$repo/data/books/v2/books.db"
$legs = "$repo/data/books/v2/books-legs.db"
$exe = "$repo/src/MovieTheater.BooksHost/bin/Debug/net10.0/MovieTheater.BooksHost.exe"
Set-Location $repo

function Step([string]$label, [scriptblock]$body) {
    Write-Host ""
    Write-Host ("=" * 96)
    Write-Host "== $label"
    Write-Host ("=" * 96)
    & $body
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "STOP: '$label' exited $LASTEXITCODE. The wave is halted here." -ForegroundColor Red
        Write-Host "      Nothing further runs. Restore from the backup printed above if the DB was already written."
        exit $LASTEXITCODE
    }
}

# @( ) keeps it an array: one batch would otherwise be a bare string, and `@files` below splats a string
# character by character (wave 15, R-030 alone, reached apply_identity as the batch "F").
$files = @($Batches | ForEach-Object { "$repo/docs/books/identity/decisions/$_.txt" })
$missing = $files | Where-Object { -not (Test-Path $_) }
if ($missing) {
    Write-Host "STOP: no decision file for: $($missing -join ', ')" -ForegroundColor Red
    exit 2
}
Write-Host "wave: $($Batches -join ', ')  (kinds: $((($Batches | ForEach-Object { $_.Substring(0,1) }) | Sort-Object -Unique) -join ' '))"

Step "check_identity --all (cross-file merge and duplicate rules, not just this wave's grammar)" { python "$tools/check_identity.py" --all }
Step "backup_live (SQLite online backup of books.db + books-legs.db)" { python "$ctools/backup_live.py" }
Step "apply_identity --apply (undo jsonl flushed per batch; prints the MERGE EXPOSURE list)" { python "$tools/apply_identity.py" @files --apply }

# The merge does not exist until the resolve makes it, so the containment repair cannot run before it —
# but the snapshot it compares against can only be taken while the old shelves still stand. Hence: snapshot,
# resolve, then measure what moved. Wave 1 skipped this and stopped at audit_containment.
$snap = "$repo/docs/books/identity/undo/wave-$stamp-before.json"
Step "merge_refusals --snapshot (the pre-resolve map of every collected edition to its shelf)" { python "$tools/merge_refusals.py" --snapshot $snap }
Step "books-resolve --series (rebuilds Series from SeriesKeyLink; MERGES and RENAMES)" { & $exe books-resolve --series --db $db --legs $legs }
Step "merge_refusals --apply (containment refusals for every edition the merge moved)" { python "$tools/merge_refusals.py" --from $snap --wave $Wave --apply }
Step "check_decisions (the survivors' files must cover their shelves again)" { python "$ctools/check_decisions.py" }
Step "pass2 (re-expand the decision files into pass2_spans.jsonl)" { python "$ctools/pass2.py" }
Step "books-curated-spans-import --apply (land the re-expanded ranges)" { & $exe books-curated-spans-import --in "$ctools/pass2_spans.jsonl" --db $db --batch "identity-wave-$Wave" --apply }
Step "run_chain3.ps1 (collected-editions -> reading-order -> containment)" { pwsh "$repo/run_chain3.ps1" }

# ── the PLAN §8 standing checks. A non-zero in any of these is a stop, not a note. ──────────────
Step "audit_containment" { python "$ctools/audit_containment.py" }
Step "overclaim_check" { python "$ctools/overclaim_check.py" }
Step "overlap_check --all (only GL v4 S94612 and MMPR S98522 are documented exceptions)" { python "$ctools/overlap_check.py" --all }
Step "check_decisions (976 files, 0 failing)" { python "$ctools/check_decisions.py" }
Step "audit_issue_details (0 defects, 3 understood leads)" { python "$ctools/audit_issue_details.py" }
Step "armed_unjudged_check (0 with LIVE EXPOSURE)" { python "$ctools/armed_unjudged_check.py" }
Step "coverage_ledger (0 unaccounted)" { python "$ctools/coverage_ledger.py" }
Step "audit_identity (0 failures)" { python "$tools/audit_identity.py" }
Step "identity_coverage (both partitions sum; the shelf count must have fallen by exactly the merge-with count)" { python "$tools/identity_coverage.py" }
# PLAN §8 gives this line as `tools/runsql.py` + `tools/sql/series_report.sql`; there is no `tools/` at the
# repo root. Both live under the containment tools directory, and that is what is called here.
Step "Saga S14966 UNCHANGED (compare against sql/saga_reference.txt)" { python "$ctools/runsql.py" $db "$ctools/sql/series_report.sql" ":sid=14966" }

Write-Host ""
Write-Host "WAVE LANDED: $($Batches -join ', ')" -ForegroundColor Green
Write-Host "Compare the identity_coverage shelf count against the previous wave: it must have moved by the"
Write-Host "number of 'merge-with' flags landed and by nothing else (PLAN §8)."
