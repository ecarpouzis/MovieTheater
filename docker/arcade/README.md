# Arcade (CloudRetro) stack — Ziggy setup

The emulator half of the arcade (docs/arcade-plan.md §4). The site half (gateway, backend, `/arcade`
page) is separate; this is what runs the actual games on Ziggy and streams them over WebRTC.

## How much is automated

**Automated:**
- **Cores** — `repo.sync: true` (config.yaml) auto-downloads every libretro core from the buildbot on
  first boot. No manual core files, ever. Cached in a volume so restarts don't re-fetch.
- **The stack** — `docker compose up -d` brings up the coordinator + 3 workers + Xvfb.
- **The catalog** — `arcade-ingest` scans the ROM tree and creates/updates `ArcadeGame` rows. No
  hand-entry of games; re-runnable, resumable, never deletes.

**Manual (can't be automated):**
- **The ROMs themselves** — you supply them, filed into the per-system subfolders below.
- **PS1 BIOS** — `scph5501.bin` in the worker's libretro system dir (copyrighted; PCSX-ReARMed has a
  reduced-compatibility HLE fallback if absent). NES/SNES/Genesis/N64/GBA need none.
- **Router** — forward **UDP 8443–8445** → Ziggy, and a Windows Defender inbound allow for the same.
- **The image pin** — one-time build from a chosen CloudRetro commit (below).

## Building the image

CloudRetro cuts no releases, so we pin a commit and own the image:

```bash
# clone with autocrlf off — Windows CRLF on the shell scripts breaks the build (env: sh\r: not found)
git clone -c core.autocrlf=false https://github.com/giongto35/cloud-game
cd cloud-game && git checkout 13852a7            # the pinned SHA
git apply /path/to/docker/arcade/patches/*.patch  # JIT scan-on-miss (see patches/README.md)
docker build -t movietheater/cloud-game:pinned .    # uses the repo's own Dockerfile
```

Put `movietheater/cloud-game:pinned` in `.env` as `ARCADE_IMAGE`. (Record the SHA you pinned in the
plan so upgrades are deliberate.)

The `patches/` are small, reproducible source edits we own on top of the pinned SHA — apply ALL of
them before building (see `patches/README.md` for what each one does): 0001 JIT scan-on-miss,
0002 IPv4 single-port mux (required under WSL2 mirrored networking), 0003 h264/NVENC encoding.
Re-generate after bumping the SHA if one fails to apply.

## ROM layout

Under `ROMS_DIR` (Ziggy local disk, **not** the L: NAS), one subfolder per system — the names match
each core's `folder` key and `arcade-ingest`'s classifier:

```
roms/
  nes/  snes/  genesis/  gb/  gbc/  gba/  n64/  psx/  arcade/
```

Extensions per system: nes `.nes`; snes `.sfc/.smc`; genesis `.md/.gen/.smd/.bin`; gb `.gb`;
gbc `.gbc`; gba `.gba`; n64 `.n64/.z64/.v64`; psx `.cue/.chd/.pbp`; arcade `.zip`.

2D collections on `R:\Roms\Games` are zipped. PS1 plays from the `.7z` master on L: via the JIT ROM
cache (docs/arcade-jit-cache.md) — extracted on demand into `roms/psx`. **PS1 BIOS**: put
`scph1001.bin`/`scph5501.bin` in `BIOS_DIR` (`D:\ArcadeStorage\bios`) — the GPU compose mounts it at
the core system dir (`/usr/local/share/cloud-game/libretro/system`).

All arcade data lives under one root on Ziggy's fast NVMe: `D:\ArcadeStorage\{roms,saves,bios,savestore}`
(the 990 PRO — NOT the repo drive F:, a spinning HDD, and NOT the L: NAS). It's outside the repo, so
ROMs/BIOS can never be committed. `savestore/` is the durable per-user save store (docs/arcade-saves-plan.md).

## Bring-up

```bash
cp .env.example .env      # then edit: ARCADE_IMAGE, ZIGGY_PUBLIC_IP, ROMS_DIR, SAVES_DIR, BIOS_DIR
docker compose up -d
```

One-time UDP-buffer sysctl (roadmap WS-A.1 — same-host N64 packet-loss fix). CloudRetro asks the
kernel for a 16 MiB WebRTC mux buffer but it's clamped to the ~208 KiB `net.core.rmem_max` default;
raise it once (the distro's systemd re-applies it every boot):

```bash
# from inside the Ubuntu-24.04 distro as root:
cp docker/arcade/99-arcade-udp-buffers.conf /etc/sysctl.d/ && sysctl --system
sysctl net.core.rmem_max net.core.wmem_max     # verify → 25165824 (not 212992)
```

Then catalog the ROMs (from the site's CLI project — dry-run first, then apply, looping the cursor):

```bash
dotnet run --project src/MovieTheater -- arcade-ingest --roms D:\ArcadeStorage\roms            # dry run
dotnet run --project src/MovieTheater -- arcade-ingest --roms D:\ArcadeStorage\roms --apply    # write
# large libraries: repeat with --after "<nextCursor>" until remaining: 0
```

Set `ArcadeMaxConcurrentRooms` in the site config to **the number of worker services** here (3).

## Phase-0 verify items (flagged in config/compose)

- **Per-worker UDP ports** vs one shared 8443 — this compose gives each worker its own (8443/8444/8445);
  collapse if a shared port proves to work.
- **Core list merge vs replace** — if adding pcsx/gen in config.yaml drops the default cores, list the
  defaults there too.
- **Genesis core lib name** — confirm `genesis_plus_gx_libretro` against the buildbot.
- **Docker Desktop UDP proxy** under sustained media load — the one unbenchmarked unknown (Appendix E);
  fall back to WSL2 mirrored networking / a bridged Hyper-V VM if it shows loss/jitter.

## gst-nvcodec-intrarefresh.patch (the intra-refresh plugin)

Upstream GStreamer's nvcodec never exposed NVENC's intra-refresh (literal TODOs in
gstnvav1encoder.cpp). This patch adds `intra-refresh-period` / `intra-refresh-count` properties to
nvav1enc and wires them into NV_ENC_CONFIG_AV1. Build against the EXACT installed gst version
(1.28.4): fetch the gst-plugins-bad tarball, apply, `meson setup bld2 -Dnvcodec=enabled
-Dbuildtype=release` (default auto features — a minimal -Dauto_features=disabled build produces a
reduced in-tree gstd3d11 that breaks the decoder half), `ninja sys/nvcodec/libgstnvcodec.dll`, then
replace D:\msys64\ucrt64\lib\gstreamer-1.0\libgstnvcodec.dll (original kept as
.pre-intrarefresh.bak). Verified: 600 encoded frames -> exactly 1 keyframe with
gop-size=-1 intra-refresh-period=120 intra-refresh-count=15. ⚠ A gst package upgrade OVERWRITES the
plugin — rebuild this patch after any MSYS2 gst update, and always deploy with worker patch 0029
(PLI responder) or loss recovery has no path.
