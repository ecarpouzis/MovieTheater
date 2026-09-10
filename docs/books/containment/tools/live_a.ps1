$ErrorActionPreference = 'Stop'
$PR  = 'F:\Work\MovieTheater\src\MovieTheater.BooksHost\MovieTheater.BooksHost.csproj'
$DB  = 'F:\Work\MovieTheater\data\books\v2\books.db'
$LG  = 'F:\Work\MovieTheater\data\books\v2\books-legs.db'
$ARC = 'F:\Work\MovieTheater\data\books\archive'

function Step($name, $argv) {
  Write-Host ""
  Write-Host "===== $name =====" -ForegroundColor Cyan
  & dotnet run --project $PR --no-build -- @argv
  if ($LASTEXITCODE -ne 0) { throw "$name failed with $LASTEXITCODE" }
}

Step 'db-migrate' @('books-db-migrate', '--db', $DB, '--legs', $LG)
Step 'reparse (dry)' @('books-reparse', '--db', $DB, '--top', '5')
Step 'reparse (apply)' @('books-reparse', '--db', $DB, '--top', '0', '--apply')
Step 'resolve --series' @('books-resolve', '--db', $DB, '--legs', $LG, '--series')
Step 'cv-descriptions-import' @('books-cv-descriptions-import', '--rip', "$ARC\mybooks\comicdb_comicvine_20260122.db", '--legs', $LG, '--apply')
Step 'locg-reprints-import' @('books-locg-reprints-import', '--dir', "$ARC\locg_cache\reprints", '--legs', $LG, '--apply')
Step 'cv-spans' @('books-cv-spans', '--db', $DB, '--legs', $LG, '--top', '0', '--apply')
Step 'gcd-spans' @('books-gcd-spans', '--db', $DB, '--gcd', "$ARC\mybooks\GrandComicsDatabase-06-01-06.db", '--top', '0', '--apply')
Step 'locg-editions' @('books-locg-editions', '--db', $DB, '--legs', $LG, '--rich', "$ARC\locg_cache\rich", '--top', '0', '--apply')

Write-Host ""
Write-Host "===== LIVE A COMPLETE =====" -ForegroundColor Green
