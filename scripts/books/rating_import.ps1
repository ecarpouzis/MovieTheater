#requires -Version 7
<#
.SYNOPSIS
  Land every answered rating batch that has not landed yet: rating_pass.py --apply (answers -> import JSONL), then
  books-insight-import --apply. Idempotent: the importer skips lines already landed by SourceKey, and a batch whose
  import file is already complete is skipped by its marker. Run books-resolve + books-library-ratings afterwards.
#>
param([string]$Db = 'F:\Work\MovieTheater\data\books\v2\books.db')
$ErrorActionPreference = 'Stop'
$root = 'F:\Work\MovieTheater'
$exe = "$root\src\MovieTheater.BooksHost\bin\Debug\net10.0\MovieTheater.BooksHost.exe"
$marks = "$root\docs\books\ratings\import\landed.txt"
$landed = if (Test-Path $marks) { Get-Content $marks } else { @() }
foreach ($a in Get-ChildItem "$root\docs\books\ratings\answers" -Filter 'B-*.jsonl' | Sort-Object Name) {
  $name = $a.BaseName
  if ($landed -contains $name) { continue }
  python "$root\scripts\books\rating_pass.py" --apply $a.FullName | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "rating_pass --apply failed on $name" }
  & $exe books-insight-import --file "$root\docs\books\ratings\import\$name.jsonl" --db $Db --apply | Select-Object -Last 1 | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "books-insight-import failed on $name" }
  Add-Content -Path $marks -Value $name
}
'next: books-resolve, then books-library-ratings'
