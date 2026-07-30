#!/bin/bash
# Calibrate a core's bits-per-pixel-per-frame target for the DERIVED bitrate ceiling
# (cloud-game-gl pkg/worker/abr.go autoCeilingKbps, and the per-core `bppTarget:` override).
#
# WHY THIS EXISTS
# ---------------
# The ceiling is computed as  bpp x encodedPixels x fps, and `bpp` was originally two hand-picked
# constants (0.50 for nearest-magnified 2D, 0.18 for smooth/3D) inferred from ONE core's behaviour
# each. That is thin evidence for eleven systems, and the class heuristic is a proxy: it reads the
# EDGE characteristic (a nearest magnify makes step-function edges that cost bits) but is blind to
# content complexity, and cannot tell point-sampled 3D from a sprite sheet. pcsx_rearmed is the
# standing example - nearest at x2, but its content is PSX 3D, not a sprite sheet.
#
# This measures it instead: encode the core's OWN frames through the PRODUCTION encoder at a range of
# bitrates and find where SSIM stops improving. That knee, divided by (pixels x fps), is the bpp this
# core actually needs. Put the result in the worker config as `bppTarget:` for that core.
#
# STEP 1 - CAPTURE (needs a maintenance window; it restarts the workers)
# ---------------------------------------------------------------------
#   ⚠ CHECK FOR A LIVE ROOM FIRST. `curl -s localhost:8000/status` - every worker must be free.
#     (Learned the hard way 2026-07-30: this killed a live session.)
#   ⚠ STOP THE WATCHDOG FIRST, or it will re-spawn runners while you tear them down and you will
#     spend twenty minutes fighting your own self-healing:
#       Stop-ScheduledTask 'MovieTheater - Arcade GL Worker Watchdog'
#   Then, in PowerShell:
#     [Environment]::SetEnvironmentVariable('CLOUD_GAME_FRAME_DUMP_DIR','D:\ArcadeStorage\framedump\<tag>','User')
#     [Environment]::SetEnvironmentVariable('CLOUD_GAME_FRAME_DUMP_COUNT','600','User')   # 10s at 60fps
#     [Environment]::SetEnvironmentVariable('CLOUD_GAME_FRAME_DUMP_SKIP','600','User')    # skip boot
#   ⚠ The workers must be RESTARTED THROUGH THEIR TASKS to inherit the env. `schtasks /end` does NOT
#     kill the runner's children - stop the tasks, drop the `.stop` sentinel in each ConfDir, wait for
#     the workers to exit, kill any surviving run-arcade-glworker.ps1, THEN Start-ScheduledTask each.
#   Play the game long enough to pass SKIP+COUNT frames (~25s of actual gameplay, not menus), then
#   CLEAR the three env vars, restart the tasks the same way, and restart the watchdog.
#
#   Pick content that is HARD to encode - a full-screen scroll (Sonic), not a title card. The knee of
#   a static screen tells you nothing about the case that actually blocks.
#
# STEP 2 - MEASURE (this script; safe, fully offline, touches no worker)
# ---------------------------------------------------------------------
#   bash calibrate-arcade-bpp.sh <dumpdir> <encW> <encH> [fps]
#     e.g. bash calibrate-arcade-bpp.sh D:/ArcadeStorage/framedump/gen-cal 960 672 60
#   encW/encH are the PRODUCTION encode size (core viewport x scale) - read it off the worker's own
#   `abr: auto ceiling ... encode WxH` line, do not compute it by hand.
#
# READING THE RESULT — LOOK AT THE TAIL, NOT THE AVERAGE
#   ⚠ Mean and median SSIM are USELESS here and will tell you every bitrate is fine. Measured on 600
#   Genesis frames: per-frame MEDIAN was identical (0.99994) from 5 to 25 Mbps, while the WORST frames
#   went 0.9957 -> 0.9998. A frame-wide mean averages localized blocking over ~645k pixels, and
#   localized blocking is the whole complaint. (Same lesson the input-latency probe learned: detect
#   per-block, not frame-wide.) So this script reports p05/p01/worst, and those are what to read.
#   Y (luma) is the column that matters; chroma saturates early and flatters a low bitrate.
#
#   Do not assume a knee exists. For scroll-heavy pixel art the tail kept improving across the whole
#   range with no flattening, which means "pick the knee" has no answer and the right target is
#   whatever lands near abrAutoMaxKbps. A 3D core may well have a real knee; measure it.
set -euo pipefail

DUMP="${1:?usage: calibrate-arcade-bpp.sh <dumpdir> <encW> <encH> [fps]}"
EW="${2:?encode width}"
EH="${3:?encode height}"
FPS="${4:-60}"

export PATH="/d/msys64/ucrt64/bin:$PATH"   # ⚠ the GStreamer WITH nvcodec. /c/msys64/mingw64 is the
                                           # Go/CGO toolchain and has no nvenc elements at all.
FF="/c/Program Files/Jellyfin/Server/ffmpeg.exe"

# The sidecar records how the core actually laid the pixels out; never assume.
SIDE=$(ls "$DUMP"/frame-*.json | head -1)
W=$(grep -o '"width":[ ]*[0-9]*' "$SIDE" | grep -o '[0-9]*')
H=$(grep -o '"height":[ ]*[0-9]*' "$SIDE" | grep -o '[0-9]*')
BPP_SRC=$(grep -o '"bpp":[ ]*[0-9]*' "$SIDE" | grep -o '[0-9]*')
STRIDE=$(grep -o '"stride":[ ]*[0-9]*' "$SIDE" | grep -o '[0-9]*')
# stride is in BYTES and usually exceeds width - libretro pitch. Parse the padded row, then crop.
SRCW=$((STRIDE / BPP_SRC))
CROP=$((SRCW - W))
case "$BPP_SRC" in
  2) GFMT=rgb16 ;;                       # RETRO_PIXEL_FORMAT_RGB565
  4) GFMT=bgrx ;;                        # XRGB8888
  *) echo "unhandled source bpp=$BPP_SRC" >&2; exit 1 ;;
esac
echo "source ${W}x${H} bpp=$BPP_SRC stride=$STRIDE -> parse ${SRCW} wide, crop $CROP; encode ${EW}x${EH}@${FPS}"

cd "$DUMP"
cat frame-*.bin > all.raw
SRC_CHAIN="filesrc location=all.raw ! rawvideoparse width=$SRCW height=$H format=$GFMT framerate=$FPS/1 \
 ! videocrop right=$CROP ! videoconvertscale method=nearest-neighbour"

# Reference = the same upscale production applies, unencoded. Compare against THIS, not the raw core
# frame, or the scaler's own error contaminates every measurement.
gst-launch-1.0 -q $SRC_CHAIN ! video/x-raw,format=I420,width=$EW,height=$EH ! filesink location=ref.i420 >/dev/null 2>&1

# ⚠ nvav1enc/nvh264enc take NV12 ONLY. Feeding I420 fails to LINK, and gst-launch reports that as a
# warning while still exiting 0 - so the encode silently produces nothing and every SSIM reads ERR.
P="preset=p6 tune=ultra-low-latency rc-mode=cbr gop-size=-1 intra-refresh-period=120 intra-refresh-count=15 temporal-layers=3 spatial-aq=true zerolatency=true"

echo "kbps,bpp,ssimY"
for B in 5000 8000 12000 16000 20000 25000 32000; do
  gst-launch-1.0 -q $SRC_CHAIN ! video/x-raw,format=NV12,width=$EW,height=$EH \
    ! nvav1enc bitrate=$B $P ! av1parse ! matroskamux ! filesink location=o_$B.mkv >/dev/null 2>&1
  Y=$("$FF" -nostats -hide_banner -loglevel info -i o_$B.mkv \
        -f rawvideo -pix_fmt yuv420p -s ${EW}x${EH} -r $FPS -i ref.i420 \
        -lavfi "[0:v][1:v]ssim" -f null - 2>&1 | grep -o 'Y:[0-9.]*' | head -1 | cut -d: -f2)
  awk -v b=$B -v y="${Y:-ERR}" -v px=$((EW*EH)) -v f=$FPS \
      'BEGIN{printf "%d,%.3f,%s\n", b, b*1000/(px*f), y}'
  rm -f o_$B.mkv
done
rm -f all.raw ref.i420
echo DONE
