<#
.SYNOPSIS
    Regenerates docker/arcade/patches/fork.patch from the CloudRetro fork branch, and PROVES it.

.DESCRIPTION
    The arcade runs on a fork of CloudRetro, not on upstream. The fork lives in two places and they
    must not disagree:

      branch      `movietheater-fork` in the cloud-game checkout (also pushed to our private
                  cloud-game-gl fork repo — the `github` remote of that checkout) — the source of
                  truth, and what worker.exe is built from.
      fork.patch  a generated diff of that branch against upstream `13852a7` — the thing that lets
                  anyone rebuild the worker from a clean checkout of upstream.

    fork.patch is GENERATED, never hand-edited. That is the whole point: the numbered patches in
    patches/ used to be maintained by hand, and by 2026-07-13 they had rotted into a backup that
    could not restore anything — four of them no longer applied in sequence, two files were captured
    in none of them, and 0030 had never included its C header change, so the chain would not even
    compile. Nobody noticed, because nothing ever tried.

    So this script does not just export: it VERIFIES, by applying the patch to a pristine upstream
    worktree and building the worker from it. A backup that has never been restored is not a backup.

    It also checks that git hands the file back UNCHANGED (`-text`, no eol rewriting). That is a
    separate failure and the nastier one: for a long time git rewrote fork.patch's line endings on
    checkout, so the patch a fresh clone received did not apply at all, while this script still
    reported success — because it had only ever verified the bytes it wrote itself, never the bytes
    git restores. Proving the on-disk file is not the same as proving the committed one.

    Run it after any change to the fork (and commit the resulting fork.patch).

.PARAMETER Repo      The cloud-game checkout holding the fork branch.
.PARAMETER Base      Upstream commit the fork sits on.
.PARAMETER Branch    The fork branch.
.PARAMETER SkipBuild Export + apply-check only (fast); skips the compile proof.
#>
param(
    [string]$Repo   = "D:\Arcade\build\cloud-game-gl",
    [string]$Base   = "13852a7",
    [string]$Branch = "movietheater-fork",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$out      = Join-Path $repoRoot "docker\arcade\patches\fork.patch"
$scratch  = Join-Path (Split-Path $Repo -Parent) "cg-verify-fork"

# git writes progress to stderr, which Windows PowerShell turns into a terminating error under
# `$ErrorActionPreference = Stop`. Run native commands with it relaxed and judge them by exit code,
# which is the only thing that actually says whether they worked.
function Invoke-Native {
    param([scriptblock]$Cmd, [string]$What)
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $Cmd } finally { $ErrorActionPreference = $old }
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
}

Write-Host "exporting $Base..$Branch -> $out"
# git writes the file itself (--output). Do NOT pipe it through Set-Content: PowerShell 5.1 would
# stamp a BOM on it and rewrite the line endings as CRLF, and `git apply` then rejects its own diff.
Invoke-Native { & git -C $Repo diff $Base $Branch --output="$out" } "git diff"

$dirty = & git -C $Repo status --short
if ($dirty) {
    Write-Warning "the fork checkout has UNCOMMITTED changes — they are NOT in fork.patch:"
    $dirty | ForEach-Object { Write-Warning "  $_" }
    Write-Warning "commit them to $Branch and re-run, or the exported patch is already a lie."
}

# Prove git will hand this file BACK unchanged.
#
# Everything below verifies the bytes sitting on disk right now — which is not what anyone else ever
# gets. They get whatever git restores on checkout, and for a long time those were not the same file:
# fork.patch's CONTENT legitimately holds CR bytes (the fork's sources are CRLF), the repo has
# core.autocrlf=true, and with no .gitattributes rule git rewrote every line on checkout. 72,004 CRs
# became 91,664, the file grew 19,660 bytes, and `git apply` rejected every hunk. The rebuild path was
# broken from any fresh clone while this script cheerfully printed "the backup is real" — it had
# validated the pre-round-trip bytes and never the restored ones (found 2026-07-30).
#
# `git hash-object` with filters vs --no-filters is the cheap, commit-free detector: if the checkin
# filter alters a single byte, the two OIDs differ, and a file git rewrites on the way IN is a file it
# rewrites on the way OUT. `-text` pins both directions to a no-op.
$relOut = "docker/arcade/patches/fork.patch"
$attr = (& git -C $repoRoot check-attr text -- $relOut) -replace '^.*:\s*text:\s*', ''
$oidFiltered = & git -C $repoRoot hash-object $relOut
$oidVerbatim = & git -C $repoRoot hash-object --no-filters $relOut
if ($oidFiltered -ne $oidVerbatim) {
    throw ("git would REWRITE fork.patch on checkin/checkout (text attr = '$attr'; filtered $oidFiltered " +
           "vs verbatim $oidVerbatim). Whatever this run proves about the file on disk, a fresh clone " +
           "gets different bytes and `git apply` will reject them. Fix: keep '$relOut -text' in " +
           ".gitattributes, then re-export and re-add — a blob committed BEFORE that rule already had " +
           "its CRs stripped on the way in, so the rule alone does not repair it.")
}
# The OID check above only fires once a patch actually CONTAINS CRs. Without the rule the hazard is
# latent: today's export may be pure-LF and pass, and the breakage lands on whoever regenerates next.
# "unset" is what `-text` reports and is the state we want; "unspecified" means no rule matched.
if ($attr -ne "unset") {
    Write-Warning ("$relOut is not marked -text (text attr = '$attr'). Today's bytes survive the " +
                   "round trip, but nothing is stopping git from rewriting line endings the moment a " +
                   "future export contains CRs. Add '$relOut -text' to .gitattributes.")
} else {
    Write-Host "git round-trip is byte-exact (-text in force)"
}

# Prove it: a pristine upstream tree + this patch must reproduce a worker that compiles.
if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
Invoke-Native { & git -C $Repo worktree prune } "worktree prune"
Invoke-Native { & git -C $Repo worktree add --detach $scratch $Base 2>$null | Out-Null } "worktree add"
try {
    Invoke-Native { & git -C $scratch apply $out } "fork.patch does NOT apply to $Base - the export is broken"
    Write-Host "applies cleanly to $Base"

    if (-not $SkipBuild) {
        $env:PATH        = "D:\msys64\ucrt64\bin;$env:PATH"
        $env:CGO_ENABLED = "1"
        $env:GOPATH      = "D:\Arcade\build\go"
        $env:GOCACHE     = "D:\Arcade\build\gocache"
        Push-Location $scratch
        try {
            Invoke-Native { & go build -o "$scratch\worker-verify.exe" ./cmd/worker } "the patched tree does NOT compile"
        } finally { Pop-Location }
        Write-Host "builds from the patch alone - the backup is real"
    }
} finally {
    $ErrorActionPreference = "Continue"
    & git -C $Repo worktree remove --force $scratch 2>$null
    & git -C $Repo worktree prune 2>$null
    $ErrorActionPreference = "Stop"
}

Write-Host "`nfork.patch is current. Commit it, and push the branch:  git -C $Repo push github $Branch"
