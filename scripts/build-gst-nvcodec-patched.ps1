<#
.SYNOPSIS
    Rebuilds the PATCHED GStreamer nvcodec plugin (libgstnvcodec.dll) — NVENC intra-refresh +
    AV1 temporal SVC — against the exact installed GStreamer.

.DESCRIPTION
    Two things upstream's nvcodec cannot do, both of which the arcade depends on:

      1. Intra-refresh (`intra-refresh-period` / `intra-refresh-count` on nvav1enc). Upstream leaves
         literal TODOs. Infinite GOP + intra-refresh = a keyframe-free stream, which is why the
         arcade has no periodic keyframe bursts to pace around.

      2. Temporal SVC (`temporal-layers`). NVENC emits a hierarchical-P pyramid whose upper layers
         are NOT referenced by the base, so the SENDER can drop them per-receiver. That is what lets
         one weak peer take 30fps (or 15) while the rest of the room keeps 60 — instead of ABR
         dragging the whole room's single encode down to the worst peer.

    The temporal-SVC half needs the **SDK 13** nvEncodeAPI.h: SDK 12's NV_ENC_CONFIG_AV1 has
    maxTemporalLayersMinus1 but neither enableTemporalSVC nor numTemporalLayers, so there is no field
    to switch it on. The GPU is NOT the constraint — an Ada AD104 (RTX 4070 Ti) reports
    NV_ENC_CAPS_SUPPORT_TEMPORAL_SVC=1 with 3 layers for AV1. It was only ever the header.

    ⚠ REQUIRES an NVENC 13.0 driver. The patch flips USE_STATIC_SDK_VER to 1 so struct versions come
    from the header; with a <13.0 driver the encoder would reject every config. Check with
    NvEncodeAPIGetMaxSupportedVersion (596.21 reports 13.0).

      3. Reference discipline (`strict-refs`, patch 0003, default ON). Upstream leaves the DPB at the
         driver's default and the forward-ref count at AUTOSELECT, so under our infinite GOP a
         reference slot can hold one picture for the whole life of a room — and a frame predicted
         from it with a null residual decodes to that picture, PRISTINE, with a brand-new timestamp.
         That is the stale-frame artefact the 2026-08-08 spectator reels caught nine times. 0003
         pins the DPB to the temporal ladder's depth, numFwdRefs/numRefL0 to 1, and LTR off, for
         BOTH codecs. Verified: the AV1 tid histogram (75/75/150) and the H.264 nal_ref_idc
         alternation (150 ref / 150 non-ref) are unchanged, so neither ladder is harmed.

    ⚠ RE-RUN AFTER ANY `pacman -Syu` THAT TOUCHES GSTREAMER — the upgrade silently restores the stock
    DLL and both features vanish (SVC rooms then fall back to plain 60fps for everyone; intra-refresh
    loss is worse — no keyframes at all unless gop-size is also changed back).

.NOTES
    Supersedes docker/arcade/gst-nvcodec-intrarefresh.patch, which is CONTAINED in
    patches/gst/0002-nvcodec-temporal-svc.patch. Do not apply both.
#>
param(
    # Build the DLL and STOP — do not touch the installed plugin. Use this while workers are up:
    # the install step deliberately refuses to run against a live worker, and a rebuilt artifact
    # sitting on disk is what a next session actually wants to find.
    [switch]$BuildOnly,
    [string]$Version = "",
    [string]$Ucrt64  = "D:\msys64\ucrt64",
    [string]$WorkDir = "D:\Arcade\build\gst",
    # nv-codec-headers tag carrying the SDK 13.0 nvEncodeAPI.h (NVIDIA's header, redistributed).
    [string]$SdkTag  = "n13.0.19.0"
)

$ErrorActionPreference = "Stop"
$env:Path = "$Ucrt64\bin;$env:Path"

$repo   = Split-Path $PSScriptRoot -Parent
# Applied IN ORDER. 0003 depends on 0002 — it extends the same property tables.
$patches = @(
    (Join-Path $repo "docker\arcade\patches\gst\0002-nvcodec-temporal-svc.patch"),
    (Join-Path $repo "docker\arcade\patches\gst\0003-nvcodec-strict-refs.patch")
)
$target = Join-Path $Ucrt64 "lib\gstreamer-1.0\libgstnvcodec.dll"
foreach ($p in $patches) { if (-not (Test-Path $p)) { throw "patch not found: $p" } }

# The plugin must be built against the GStreamer that will load it.
if (-not $Version) {
    $v = (& "$Ucrt64\bin\gst-inspect-1.0.exe" --version |
          Select-String -Pattern 'gst-inspect-1.0 version ([\d.]+)').Matches[0].Groups[1].Value
    if (-not $v) { throw "could not determine installed GStreamer version" }
    $Version = $v
}
Write-Host "Building gst-plugins-bad $Version (nvcodec only) against the installed GStreamer."

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$src = Join-Path $WorkDir "gst-plugins-bad-$Version"
$tar = Join-Path $WorkDir "gst-plugins-bad-$Version.tar.xz"

# Always build from a PRISTINE tree: a re-run over an already-patched tree would fail to apply and
# (worse) could leave a half-patched source that still compiles.
if (Test-Path $src) { Remove-Item -Recurse -Force $src }
if (-not (Test-Path $tar)) {
    $url = "https://gstreamer.freedesktop.org/src/gst-plugins-bad/gst-plugins-bad-$Version.tar.xz"
    Write-Host "downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $tar
}
& tar -xf $tar -C $WorkDir
if (-not (Test-Path $src)) { throw "extract failed: $src" }

# Vendor the SDK 13 header over the tarball's SDK 12 one.
$hdrUrl = "https://raw.githubusercontent.com/FFmpeg/nv-codec-headers/$SdkTag/include/ffnvcodec/nvEncodeAPI.h"
Write-Host "vendoring SDK 13 nvEncodeAPI.h ($SdkTag)"
Invoke-WebRequest -Uri $hdrUrl -OutFile (Join-Path $src "sys\nvcodec\nvEncodeAPI.h")
$maj = Select-String -Path (Join-Path $src "sys\nvcodec\nvEncodeAPI.h") -Pattern '#define NVENCAPI_MAJOR_VERSION (\d+)'
if ($maj.Matches[0].Groups[1].Value -ne "13") { throw "vendored header is not SDK 13 — got $($maj.Matches[0].Groups[1].Value)" }

Push-Location $src
try {
    foreach ($p in $patches) {
        & patch -p1 --forward -i $p
        if ($LASTEXITCODE -ne 0) { throw "patch failed to apply: $p" }
    }

    # Default auto features: a minimal -Dauto_features=disabled build produces a reduced in-tree
    # gstd3d11 that breaks the decoder half.
    & meson setup bld2 -Dnvcodec=enabled -Dbuildtype=release
    if ($LASTEXITCODE -ne 0) { throw "meson setup failed" }
    & ninja -C bld2 sys/nvcodec/libgstnvcodec.dll
    if ($LASTEXITCODE -ne 0) { throw "ninja build failed" }
} finally { Pop-Location }

$dll = Join-Path $src "bld2\sys\nvcodec\libgstnvcodec.dll"
if (-not (Test-Path $dll)) { throw "build produced no DLL" }

if ($BuildOnly) {
    Write-Host "BuildOnly: not installing. Artifact: $dll ($((Get-Item $dll).Length) bytes)"
    Write-Host "To verify it WITHOUT installing, copy it — KEEPING THE FILENAME, gst derives the plugin"
    Write-Host "name from it — into an empty dir, then point GST_PLUGIN_PATH and a private GST_REGISTRY"
    Write-Host "at that dir and gst-inspect-1.0 nvav1enc / nvh264enc."
    return
}

# The DLL is loaded by any running worker — it cannot be replaced while one is up.
$live = Get-Process worker -ErrorAction SilentlyContinue
if ($live) { throw "worker.exe is running (pids: $($live.Id -join ',')) — stop the worker tasks first, and check localhost:8000/status for live rooms before you do" }

$backup = Join-Path "D:\ArcadeStorage\backup" "libgstnvcodec.dll.stock-$Version"
if (-not (Test-Path $backup)) { Copy-Item $target $backup -Force; Write-Host "stock DLL saved: $backup" }
Copy-Item $dll $target -Force
Write-Host "installed: $target"

# Prove BOTH features are actually exposed — a silently-stock DLL is the failure mode this guards.
$props = & "$Ucrt64\bin\gst-inspect-1.0.exe" nvav1enc
foreach ($p in @("temporal-layers", "intra-refresh-period", "intra-refresh-count", "strict-refs")) {
    if ($props -match [regex]::Escape($p)) { Write-Host "  verified: $p" }
    else { throw "installed plugin does NOT expose $p — the build or install did not take" }
}
Write-Host "OK — patched nvcodec installed and verified."
