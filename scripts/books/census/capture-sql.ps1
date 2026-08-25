#requires -Version 7
<#
.SYNOPSIS
  R0 step 6a-c: run the LIVE MyBooks binary against the census COPY on a spare port with EF SQL
  logging on, let CacheWarmupService warm the hot set, drive the endpoints the warmer skips, then
  stop OUR process. The nssm MyBooks service is never touched.

  Everything this process can write is redirected under data/books/census (DB copy, cache dir,
  keyvault scratch); the live mybooks.db / cache / keyvault are not referenced at all.

.EXAMPLE
  pwsh -File scripts/books/census/capture-sql.ps1
#>
[CmdletBinding()]
param(
  [int]$Port = 21999,
  [string]$Exe = 'F:\Work\MyBooks\MyBooks\publish\win-x64\Mybooks.exe',
  [string]$EnvFile = 'F:\Work\MyBooks\MyBooks\e2e\.env',
  [int]$WarmTimeoutSec = 900
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$census = Join-Path $repo 'data\books\census'
$db = Join-Path $repo 'data\books\v1\mybooks-v1-census.db'
if (-not (Test-Path $db)) { throw "census copy missing: $db (run freeze.py)" }
$cache = Join-Path $census 'cache'
$kv = Join-Path $census 'keyvault\scratch.db'
$log = Join-Path $census 'ef-sql.log'
$err = Join-Path $census 'ef-sql.err.log'
New-Item -ItemType Directory -Force $cache, (Split-Path $kv) | Out-Null
Remove-Item $log, $err -ErrorAction SilentlyContinue

# Refuse to run if something already listens on the port (never fight the real service).
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw "port $Port already in use" }

$envVars = @{
  ASPNETCORE_ENVIRONMENT = 'Development'
  Mybooks__Port = "$Port"
  ConnectionStrings__ComicDb = "Data Source=$db"
  ConnectionStrings__KeyVaultDb = "Data Source=$kv"
  Mybooks__CacheDirectory = $cache
  Mybooks__ScanOnStartup = 'false'
  Mybooks__ScanIntervalMinutes = '0'
  Mybooks__WarmupThumbnailsOnStartup = 'false'
  Logging__LogLevel__Default = 'Information'
  'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command' = 'Information'
}
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.WorkingDirectory = Split-Path $Exe -Parent
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
foreach ($k in $envVars.Keys) { $psi.Environment[$k] = $envVars[$k] }
$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
$outWriter = [System.IO.StreamWriter]::new($log, $false, [System.Text.Encoding]::UTF8); $outWriter.AutoFlush = $true
$errWriter = [System.IO.StreamWriter]::new($err, $false, [System.Text.Encoding]::UTF8); $errWriter.AutoFlush = $true
$null = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action { if ($EventArgs.Data) { $Event.MessageData.WriteLine($EventArgs.Data) } } -MessageData $outWriter
$null = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action { if ($EventArgs.Data) { $Event.MessageData.WriteLine($EventArgs.Data) } } -MessageData $errWriter
[void]$proc.Start()
$proc.BeginOutputReadLine(); $proc.BeginErrorReadLine()
Write-Host "started pid $($proc.Id) on :$Port against $db"

try {
  # wait for the startup warm cycle
  $deadline = (Get-Date).AddSeconds($WarmTimeoutSec)
  $warm = $false
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $outWriter.Flush()
    if ($proc.HasExited) { throw "process exited early (exit $($proc.ExitCode)); see $err" }
    # read with FileShare.ReadWrite: Select-String cannot open the file while our writer holds it
    $fs = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite'); $sr = New-Object System.IO.StreamReader($fs); $txt = $sr.ReadToEnd(); $sr.Dispose()
    if ($txt -match 'Cache warm \(startup\)') { $warm = $true; break }
  }
  if (-not $warm) { Write-Warning "no 'Cache warm (startup)' line within $WarmTimeoutSec s; continuing with whatever was captured" }
  else { Write-Host "startup warm complete" }

  # login (creds read from e2e/.env, never printed)
  $envText = Get-Content $EnvFile
  $u = ($envText | Where-Object { $_ -like 'TEST_USERNAME=*' }) -replace '^TEST_USERNAME=', ''
  $p = ($envText | Where-Object { $_ -like 'TEST_PASSWORD=*' }) -replace '^TEST_PASSWORD=', ''
  $base = "http://localhost:$Port"
  $body = @{ username = $u.Trim().Trim('"',"'"); password = $p.Trim().Trim('"',"'") } | ConvertTo-Json
  $r = Invoke-WebRequest "$base/api/auth/login" -Method POST -ContentType 'application/json' -Body $body -SessionVariable s -SkipHttpErrorCheck
  Write-Host "login: $($r.StatusCode)"
  if ($r.StatusCode -ne 200) { throw "login failed" }

  function Hit([string]$path) {
    $resp = Invoke-WebRequest "$base$path" -WebSession $s -SkipHttpErrorCheck -TimeoutSec 300
    Write-Host ("{0,4} {1}" -f $resp.StatusCode, $path)
    return $resp
  }
  $paths = @(
    '/api/odata/catalog?$top=48&$count=true',
    '/api/odata/catalog?$top=48&$skip=48',
    '/api/odata/catalog?$top=48&$count=true&$orderby=parsedSeries asc,parsedYear asc',
    '/api/odata/catalog?$top=48&$count=true&$orderby=parsedYear desc,indexedAt desc',
    '/api/odata/catalog?$top=48&$count=true&$orderby=libraryRating desc,userRating desc,embeddedRating desc',
    '/api/odata/catalog?$top=48&$count=true&$orderby=readIndex asc',
    '/api/odata/catalog?$top=48&$count=true&$filter=publisherId eq 1',
    '/api/odata/catalog?$top=48&$count=true&$filter=parsedYear ge 1990 and parsedYear le 1999',
    "/api/odata/catalog?`$top=48&`$count=true&`$filter=contains(genre,'Action') or contains(tags,'Action') or contains(claudeTagsCsv,'Action')",
    '/api/odata/catalog?$top=48&$count=true&q=batman',
    '/api/odata/catalog?$top=48&$count=true&directory=true',
    '/api/browse/facets',
    '/api/browse/facet-options?facet=series&skip=0&top=50',
    '/api/browse/groups?groupBy=collection&groupsTop=20&perGroupTop=48',
    '/api/browse/groups?groupBy=series&groupsTop=20&perGroupTop=60',
    '/api/browse/groups?groupBy=publisher&groupsTop=20&perGroupTop=48&subGroupBy=series',
    '/api/browse/groups?groupBy=decade&groupsTop=20&perGroupTop=48',
    '/api/browse/groups?groupBy=series&groupsTop=20&perGroupTop=1&q=spider',
    '/api/browse/group-letters?groupBy=series',
    '/api/library/home',
    '/api/library/comics/suggestions?count=12',
    '/api/library/comics/latest?count=24',
    '/api/library/comics/random?count=12',
    '/api/library/comics/featured',
    '/api/library/comics/publishers',
    '/api/library/comics/events',
    '/api/library/comics/folders',
    '/api/bookmarks',
    '/api/bookmarks/history',
    '/api/user-lists',
    '/api/bookshelf/shelf-series?list=read',
    '/api/bookshelf/shelf-series?list=wantToRead',
    '/api/kids',
    '/api/kids/browse',
    '/api/kids/home',
    '/api/books?page=1&size=48',
    '/api/books/facets',
    '/opds',
    '/opds/comics?page=0&size=20'
  )
  foreach ($pth in $paths) { try { $null = Hit $pth } catch { Write-Warning "$pth : $_" } }
  # one concrete comic: take the first id from the catalog
  try {
    $cat = (Hit '/api/odata/catalog?$top=1').Content | ConvertFrom-Json
    $id = $cat.value[0].id
    if ($id) {
      foreach ($pth in @("/api/comics/$id", "/api/comics/$id/next", "/api/comics/$id/prev", "/api/comics/$id/comicvine", "/api/bookmarks/$id", "/api/thumbs/$id", "/api/browse/series/1/library-rating")) {
        try { $null = Hit $pth } catch { Write-Warning "$pth : $_" }
      }
      $sid = $cat.value[0].seriesId
      if ($sid) { try { $null = Hit "/api/browse/series/$sid/library-rating" } catch {} ; try { $null = Hit "/api/group-metadata/series/$sid" } catch {} }
    }
  } catch { Write-Warning "comic drive: $_" }
  Start-Sleep -Seconds 3
}
finally {
  if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; $proc.WaitForExit(15000) | Out-Null }
  Start-Sleep -Seconds 1
  Get-EventSubscriber | Unregister-Event
  $outWriter.Flush(); $outWriter.Dispose(); $errWriter.Flush(); $errWriter.Dispose()
  Write-Host "stopped pid $($proc.Id); log: $log ($([math]::Round((Get-Item $log).Length/1MB,1)) MB)"
  $cmds = (Select-String -Path $log -Pattern 'Executed DbCommand' | Measure-Object).Count
  Write-Host "captured DbCommands: $cmds"
}
