# Drives the unreadable-books conversion to completion.
#
# The LOOP lives here, not inside either script: each invocation of the two Python halves does a
# bounded amount of work, prints what it did and what remains, and exits 2 when a batch made no
# progress. This driver repeats them, accumulates the totals, and stops on the first no-progress
# batch, so a wedged or finished run ends by itself instead of spinning.
#
#   Phase 1  convert_unreadable.py   reads the share, writes EPUBs into a STAGING folder.
#                                    Parallel, restartable, touches nothing in Calibre.
#   Phase 2  convert_add_formats.py  runs under calibre-debug and adds each staged EPUB to its book
#                                    as an extra format. ONE calibre-debug at a time, always.
#
# Nothing is ever deleted or renamed on the share. The original unreadable file stays beside the
# new EPUB; the catalog is re-pointed afterwards by books-import-calibre, which ranks a book's
# formats and moves the EXISTING item onto the best one.
#
# Usage:
#   .\run_convert.ps1 -Plan                      # what would be done, nothing written
#   .\run_convert.ps1 -Phase 1 -Batches 1        # ONE supervised batch, watch it
#   .\run_convert.ps1 -Phase 1 -Apply            # convert to staging, to completion
#   .\run_convert.ps1 -Phase 2 -Apply            # add the staged EPUBs to Calibre
#   .\run_convert.ps1 -Report

[CmdletBinding()]
param(
    [ValidateSet('1', '2')] [string] $Phase = '1',
    [switch] $Apply,
    [switch] $Plan,
    [switch] $Report,
    [int] $Limit = 200,
    [int] $Workers = 4,
    [int] $Batches = 0,          # 0 = until there is nothing left
    [string] $Library = 'L:\6 - Books',
    [string] $Staging = 'F:\Work\MovieTheater\data\books\v2\convert-staging',
    [string] $Journal = 'F:\Work\MovieTheater\data\books\v2\convert-journal.db'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$convert = Join-Path $here 'convert_unreadable.py'
$addFmt = Join-Path $here 'convert_add_formats.py'
$calibreDebug = 'C:\Program Files\Calibre2\calibre-debug.exe'

if ($Plan) { & python $convert --library $Library --journal $Journal --plan; exit $LASTEXITCODE }
if ($Report) { & python $convert --journal $Journal --report; exit $LASTEXITCODE }

if ($Phase -eq '2') {
    # Two calibre-debug processes deadlock at ~1% CPU and look like a hung script. Refuse rather than
    # reproduce that.
    $running = Get-Process calibre-debug, calibre, calibre-server -ErrorAction SilentlyContinue
    if ($running) {
        throw ("Calibre is already running (" + (($running | ForEach-Object Name) -join ', ') +
               "). Close it before phase 2: only one process may hold the library.")
    }
    if (-not (Test-Path $calibreDebug)) { throw "calibre-debug not found at $calibreDebug" }
}

$batch = 0
$totals = @{}

while ($true) {
    $batch++
    if ($Batches -gt 0 -and $batch -gt $Batches) {
        Write-Host "`nstopping: reached the -Batches limit of $Batches"
        break
    }

    Write-Host ("`n=== phase $Phase, batch $batch " + ('=' * 50))

    if ($Phase -eq '1') {
        $argv = @($convert, '--library', $Library, '--staging', $Staging, '--journal', $Journal,
                  '--limit', $Limit, '--workers', $Workers)
        if ($Apply) { $argv += '--apply' }
        & python @argv
    }
    else {
        $argv = @('-e', $addFmt, '--', '--library', $Library, '--journal', $Journal, '--limit', $Limit)
        if ($Apply) { $argv += '--apply' }
        & $calibreDebug @argv
    }
    $code = $LASTEXITCODE

    if ($code -eq 2) {
        Write-Host "`nstopping: that batch made no progress (finished, or every remaining book fails)"
        break
    }
    if ($code -ne 0) {
        Write-Host "`nstopping: batch exited $code"
        break
    }
    if (-not $Apply) {
        Write-Host "`nstopping: dry run, one batch is enough to show the shape. Add -Apply to do it."
        break
    }
}

Write-Host "`n=== journal ===================================================="
& python $convert --journal $Journal --report

if ($Phase -eq '2' -and $Apply) {
    Write-Host @'

NEXT, in this order (nothing below is automatic):
  books-import-calibre --apply --reset    re-points each item onto its new EPUB ("upgraded" count)
  books-resolve --series
  books-thumbs                            real covers replace the generated title cards
'@
}
