$ErrorActionPreference = 'Stop'
$PR  = 'F:\Work\MovieTheater\src\MovieTheater.BooksHost\MovieTheater.BooksHost.csproj'
$DB  = 'F:\Work\MovieTheater\data\books\v2\books.db'
$LG  = 'F:\Work\MovieTheater\data\books\v2\books-legs.db'
$DOC = 'F:\Work\MovieTheater\docs\books\containment'

function Step($name, $argv) {
  Write-Host ""
  Write-Host "===== $name =====" -ForegroundColor Cyan
  & dotnet run --project $PR --no-build -- @argv
  if ($LASTEXITCODE -ne 0) { throw "$name failed with $LASTEXITCODE" }
}

Step 'curated-spans (dry)' @('books-curated-spans-import', '--in', "$DOC\curated_spans.jsonl", '--db', $DB, '--top', '0')
Step 'curated-spans (apply)' @('books-curated-spans-import', '--in', "$DOC\curated_spans.jsonl", '--db', $DB, '--top', '0', '--apply')
Step 'collected-editions' @('books-collected-editions', '--db', $DB, '--legs', $LG)
Step 'reading-order' @('books-reading-order', '--db', $DB)
Step 'containment' @('books-containment', '--db', $DB)
Step 'resolve' @('books-resolve', '--db', $DB, '--legs', $LG)
Step 'flags-import' @('books-containment-flags-import', '--in', "$DOC\flags.csv", '--db', $DB, '--prune', '--apply')
Step 'dedup-contained' @('books-dedup-contained', '--db', $DB, '--reset', '--top', '10', '--apply')

Write-Host ""
Write-Host "===== LIVE B COMPLETE =====" -ForegroundColor Green
