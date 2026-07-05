# Arcade — Windows-native GL worker (Spike B2)

**Status: scoped from source (2026-07-04), not built.** Purpose: unblock the GL 3D cores
(PSP / Dreamcast / Naomi / Atomiswave) by running **one CloudRetro worker natively on Windows**
with real NVIDIA OpenGL, alongside the existing WSL2 docker stack — sidestepping the WSLg
GLX/EGL problem entirely (`arcade-3d-cores-wslg.md`). Roadmap context: `arcade-roadmap.md` WS-B.
All facts below were verified against the **local pinned checkout `D:\Arcade\build\cloud-game`
(SHA `13852a7`)** — the exact tree we build the image from — not upstream master.

## Why this works (source-verified)

- **Upstream supports it and CI-proves it at our SHA.** `README.md` documents the MSYS2 build
  (`pacman … mingw-w64-ucrt-x86_64-{gcc,pkgconf,gstreamer,gst-plugins-base,gst-plugins-good}`);
  `.github/workflows/build.yml` builds **and runs GL tests** on `windows-latest` (UCRT64, with
  mesa-dist-win + `MESA_GL_VERSION_OVERRIDE=3.3COMPAT` for the GPU-less runner).
- **The Windows GL path is WGL on a hidden window** (`pkg/worker/caged/libretro/graphics/rgfw.go`:
  Win32 `CreateWindowExW`, `RGFW_createWindow(…, RGFW_windowHide)` at 1×1, `wglCreateContext`,
  FBO render target). On a real NVIDIA driver that context is a **GL 4.6 compatibility profile** —
  everything flycast (≥3.1 core/GLES3) and ppsspp (≥3.3) need. The WSLg failure class
  (`BadMatch`/`BadAccess` at GLX context creation on Xwayland) does not exist here.
- **Zone routing exists in the coordinator** and is the pool-isolation mechanism:
  `pkg/coordinator/worker.go:136` — `func (w *Worker) In(region string) bool { return region == ""
  || region == w.Zone }`; `hub.go` filters `find1stFreeWorker(zone)` / `findFastestWorker(zone)`
  by it, and the client's `?zone=` WS query (already present, empty, in our join descriptor)
  feeds it (`hub.go:184`).
- **Cores auto-download per-OS.** `emulator.libretro.cores.repo` (type `buildbot`,
  `https://buildbot.libretro.com/nightly`) resolves per-platform artifacts via
  `manager/repository.go` — on Windows that's the `windows/x86_64` `.dll` builds. ⚠VERIFY on
  first boot that `ppsspp_libretro.dll` / `flycast_libretro.dll` download and load.
- **Config via env works the same**: prefix `CLOUD_GAME_` (`pkg/config/loader.go:17`), e.g.
  `CLOUD_GAME_WORKER_NETWORK_ZONE`, `CLOUD_GAME_WEBRTC_SINGLEPORT`, `CLOUD_GAME_WEBRTC_ICEIPMAP`.
- **The Windows→WSL coordinator hop already works today**: the ArcadeGateway (a Windows process)
  talks to the WSL coordinator at `http://localhost:8000` in production. The worker's
  `worker.network.coordinatorAddress: localhost:8000` (`endpoint: /wso`) rides the same path.

## Build recipe (MSYS2 UCRT64, on Ziggy)

1. Install MSYS2; in a **UCRT64** shell:
   `pacman -Sy --needed git make mingw-w64-ucrt-x86_64-{gcc,pkgconf,gstreamer,gst-plugins-base,gst-plugins-good,gst-plugins-bad}`
   — `gst-plugins-bad` is the addition vs the README list: it carries the **nvcodec** plugin
   (`nvh264enc`). ⚠VERIFY with `gst-inspect-1.0 nvcodec` that the MSYS2 build ships it (it
   dlopens `nvEncodeAPI64.dll` at runtime, no CUDA SDK needed). Fallback if absent: run the
   worker with `encoder.video.codec: vp8` (vpx is in plugins-good; the 13700K shrugs at one
   640×480 stream) and revisit NVENC later.
2. Native Windows Go (same version family as the image build; `go.mod` says what the SHA wants).
   Build **inside the UCRT64 shell** so cgo finds GStreamer via pkgconf.
3. Source = a copy of the pinned tree (clone with `core.autocrlf=false`, same as the image
   workflow), then apply patches **0001, 0002, 0004, 0005** — SKIP **0003** (it patches only the
   `Dockerfile`; its job, the nvcodec plugin, is served by the `gst-plugins-bad` package here).
   All four others touch only Go code and apply cleanly to a Windows build.
4. `go build -o bin/worker.exe ./cmd/worker` (coordinator is NOT built/run on Windows — the WSL
   one stays authoritative).

## Worker configuration (worker-gl config or env)

- `worker.network`: `coordinatorAddress: localhost:8000`, `zone: gl`, `secure: false`.
- `emulator.storage` → `D:\ArcadeStorage\saves`; `library.basePath` → `D:\ArcadeStorage\roms`
  (native Windows paths — no `/mnt/d` translation on this worker); libretro **system dir** →
  `D:\ArcadeStorage\bios`. Staging (✅ verified 2026-07-04, sources on hand):
  - `dc_boot.bin`/`dc_flash.bin`/`naomi.zip`/`awbios.zip` are **already in
    `D:\ArcadeStorage\bios`** — but flat; libretro-flycast looks for them in a **`dc\`
    subfolder** of the system dir. Copy them into `D:\ArcadeStorage\bios\dc\` (keep the flat
    copies; the same mount serves the WSL workers, and the dc\ layout helps Spike B1 too).
  - **PPSSPP assets**: a real, populated assets folder already exists at
    `F:\Emulation\bios\PPSSPP` (EmuDeck-staged: flash0, lang, compat.ini, …) — copy it to
    `D:\ArcadeStorage\bios\PPSSPP\`. If the core ever complains of version skew, refresh it
    from the PPSSPP Windows zip matching the core build.
- `webrtc.singlePort: 8446` + router UDP-forward 8446→Ziggy + Windows Defender inbound UDP rule
  for worker.exe. `CLOUD_GAME_WEBRTC_ICEIPMAP` = the LAN IP (same value as `.env
  ZIGGY_PUBLIC_IP`). Patch 0002 (udp4 mux) is harmless here.
- Cores: same `psp/dc/naomi/atomiswave` entries as `docker/arcade/config.yaml` (each with
  `isGlAllowed: true` AND `coreAspectRatio: true` — the Flip contract), same `folder` pins.
- Note: a fresh JIT extraction is visible to this worker via **native fsnotify** (no
  Windows→WSL mount gap), but keep patch 0001 anyway — it's the belt to fsnotify's suspenders.

## Zoning — REQUIRED site+stack changes before mixing worker types

The coordinator hands rooms to any free worker; a PSP room on a 2D-only WSL worker just fails.
- Give **both** pools explicit zones: WSL workers `CLOUD_GAME_WORKER_NETWORK_ZONE=main` (compose
  env), Windows worker `zone: gl`.
- Site: `CloudRetroHost` puts a per-system zone in the join descriptor (`psp|dc|naomi|atomiswave
  → "gl"`, else `"main"`); the descriptor's `zone=` query already flows through the gateway
  (WsTransformer re-appends the original query string) into `hub.go:184`.
- **Trap 1 — empty zone matches ALL workers** (`Worker.In`: `region == ""` is a wildcard).
  Deploy the site zone change together with (or before) zoning the workers, or plain-2D rooms
  will land on the GL worker and eat its slot.
- **Trap 2 — `findWorkerByPreviousRoom` ignores zone** (matches by prior session/room id with
  `HasSlot()`). Once deterministic save-ids land (arcade-saves-plan), re-launching the same game
  re-pins to whichever worker last hosted that id. Same game ⇒ same zone in practice, so it's
  benign — but don't move a *system* between zones while workers still hold session history
  (worker restart clears it).
- Bump `ArcadeMaxConcurrentRooms` by 1 per added worker (advisory; t=112 stays authoritative).
  Note the cap is global, not per-zone — acceptable at friends-scale.

## Run model (operations)

- **Not a Windows service.** WGL hardware acceleration needs an interactive session (session-0
  services get software GL). Run it exactly like the WSL keepalive: a **logon Task Scheduler
  task** in the interactive session (pattern: `scripts/register-arcade-wsl-task.ps1`; add
  `scripts/register-arcade-glworker-task.ps1` that sets env, prepends MSYS2 `ucrt64\bin` to PATH
  for the GStreamer DLLs, and restarts worker.exe in a loop on exit).
- Logs to a file beside the exe; crash diagnosis = that log (the WSL teardown-segfault lore does
  not transfer; this is a different OS surface — expect different quirks).
- GPU sharing: WSL d3d12 workers + this WGL worker + NVENC sessions coexist on the 4070 Ti
  (2× NVENC engines, 8–12 session cap; we use ≤4 total).

## Spike acceptance (use the test-roms skill, PROD origin)

1. Worker boots, registers with the coordinator (visible in coordinator logs with `zone: gl`),
   boot log shows NVIDIA vendor / GL 4.x context.
2. A PSP title and a DC title reach `Playing` and HOLD: `video.fps ~55+`, `freezes: 0`, inputs
   drive gameplay. (This is exactly where WSLg died: ppsspp crashed at
   `RGFW_window_makeCurrentContext_OpenGL`, flycast at GLX BadMatch.)
3. Flagship: **Dreamcast 4-player multiplayer, two-browser test** (Power Stone 2 / MvC2 class).
4. **Transport A/B bonus:** 10-min NACK/audio-concealment comparison — a `gl`-zone room's media
   binds a native Windows socket on the LAN IP (no mirrored-relay hop at all), so this doubles
   as evidence in the WS-A audio-loss hunt.

## Pre-flight verification — DONE 2026-07-04 (all three former ⚠ items)

- ✅ **MSYS2 `mingw-w64-ucrt-x86_64-gst-plugins-bad` ships nvcodec**: the package file list on
  packages.msys2.org includes `libgstnvcodec.dll`, and its PKGBUILD builds with
  `-Dauto_features=enabled`. `nvh264enc` needs only the driver's `nvEncodeAPI64.dll`
  (System32, present). Sanity command after install: `gst-inspect-1.0 nvcodec`.
- ✅ **Buildbot Windows core DLLs exist**: HTTP 200 with real payloads at
  `https://buildbot.libretro.com/nightly/windows/x86_64/latest/` for
  `ppsspp_libretro.dll.zip` (6.4 MB) and `flycast_libretro.dll.zip` (6.1 MB) — exactly the URL
  shape `Buildbot.CoreUrl` composes (`<url>/<os>/<arch>/latest/<file><ext>.<compression>`).
  Keep the 0-byte-download retry lore from `arcade-3d-cores-wslg.md` in mind (delete stub +
  re-sync if a download flakes).
- ✅ **BIOS/assets are on hand** (see Worker configuration above): DC/Naomi/AW BIOS already in
  `D:\ArcadeStorage\bios` (needs the `dc\` subfolder copies), PPSSPP assets ready to copy from
  `F:\Emulation\bios\PPSSPP`.

## VERIFIED 2026-07-04 — the worker BUILDS, RUNS, and renders real NVIDIA GL (spike premise proven)

The B2 hypothesis is confirmed end-to-end on Ziggy, and **no elevation was needed** — Go 1.26.4 and
MSYS2 install fine as portable extracts (Go zip → `D:\Arcade\build\go`, MSYS2 base tarball →
`D:\msys64`), sidestepping the winget UAC wall.

- ✅ **Builds.** `go build ./cmd/worker` under UCRT64 (gcc 16.1.0, native Windows Go 1.26.4, CGO on,
  `GOPATH/GOCACHE` set explicitly) → `bin/worker.exe` (34.8 MB). All four Go patches (0001/0002/0004/
  0005) apply clean to `13852a7`; the only Windows cgo deps are `-lopengl32 -lgdi32` (WGL, bundled)
  + `gstreamer-video-1.0`/`gstreamer-app-1.0` (pkgconf resolves them, 1.28.4).
- ✅ **NVENC present.** `gst-inspect-1.0 nvcodec` shows `nvh264enc` — the Windows worker gets hardware
  H.264, no VP8 fallback needed. (⚠VERIFY resolved.)
- ✅ **Runs + loads our config.** Booted against a dead coordinator (isolated, zero prod impact): config
  merges (`loaded: [default config.yaml]`), zone env applies (`gl.localhost:...`), and the **GL cores
  download as Windows DLLs** — `flycast_libretro` (×3) + `ppsspp_libretro`, 200 OK from the
  `windows/x86_64` buildbot. (Two ⚠VERIFY resolved.) One gotcha: pass the ConfDir as a native Windows
  path (or set cwd there) — an MSYS2 `D:/…` arg got path-mangled and silently fell back to default; the
  PowerShell run-script passes it natively so it's fine.
- ✅ **Real NVIDIA GL rendering.** `make verify-cores` (`go test -run TestAll … -renderFrames`) rendered
  the **N64 GL fixture** (mupen64plus, `plugin_start_gfx` → WGL context) to a real textured-3D frame
  (`_rendered/windows-n64-*.png`) on Ziggy's GPU — **no GLX BadMatch, no crash**, the exact WSLg
  failure. Ziggy has no software mesa installed, so that context is the NVIDIA ICD (GL 4.6 compat).
  flycast (GL 3.1) and ppsspp (GL 3.3) share the identical `rgfw.go` WGL layer → they inherit it.

### Cutover DONE + live-room results (2026-07-04, evening)

The zoning cutover is **LIVE in prod**: `ArcadeZoningEnabled=true` (committed `appsettings.json`),
WSL workers recreated `zone=main`, gl worker joined `zone=gl`. Verified via join descriptors — 2D →
`zone=main` (WSL), psp/dc → `zone=gl`. **2D/N64 unaffected**; the routing was confirmed end-to-end by
test-roms creating live rooms that reached the gl worker.

**GL unlock re-confirmed at the core level (huge):** a live **PPSSPP** (Daxter) room on the gl worker
logged `GPU Vendor: NVIDIA GeForce RTX 4070 Ti ... 4.6.0 NVIDIA 596.21` — real NVIDIA GL 4.6, the exact
context WSLg could never create. So the platform question is settled: **yes, native Windows WGL gives
these cores real NVIDIA GL.**

**But both GL cores crash mid-boot — two DISTINCT per-core integration bugs (open):**
- **PPSSPP (psp):** `0xc0000005` in `retro_run` at "Starting graphics" — *after* the GL context/vendor
  query. Tried `ppsspp_cpu_core: IR JIT` (rules out fastmem) and `usesLibCo: true` (matches the working
  mupen thread model) — neither fixed it. Likely PPSSPP's own GL render thread vs the hw_render
  single-thread contract; needs deeper nanoarch/hw_render work.
- **flycast (dc/naomi/aw):** GPF `PC=0` (null call) *before* any GL vendor query — right after
  `retro_get_system_av_info`. BIOS is present (junctioned `system/` has both flat + `dc/` copies), so
  it's not BIOS; looks like flycast's renderer-backend/hw-render negotiation returning a null on this
  build. `usesLibCo` didn't change it.
- **N64 mupen renders fine** (verify-cores), so the shared WGL layer + `usesLibCo` path is sound — these
  are core-specific.

Config carries the attempted fixes (`usesLibCo` on all GL cores, `IR JIT` on psp) as correct-direction
settings. **Live disposition:** gl worker left OFF; psp/dc route to `gl` → "no free worker" (graceful,
same as pre-cutover — no regression). 2D/N64 fully healthy. Re-enable + keep debugging the two cores when
resumed. **Remaining to "DC multiplayer in browser":** solve the flycast null-call + PPSSPP GL-thread
crashes (per-core, likely a nanoarch hw_render patch), then router UDP 8446 + Defender rule, then the
DC 4-player two-browser flagship.

**Superseded to-do (kept for history):** ~~(a) full-pipeline proof; (b) the cutover; (c) router+firewall;
(d) flagship~~ — (a)+(b) done; (c)+(d) gated on the two per-core fixes above.

## Progress 2026-07-04 — code/config/BIOS landed; build+cutover is the remaining (elevated) work

**Done (in-repo, no live effect yet — the zoning is behind a default-OFF flag):**
- **Site zoning code** — `MovieTheaterConfiguration.ArcadeZoningEnabled` (default false) + `CloudRetroHost.ZoneForSystem`
  (psp/dc/naomi/atomiswave → `gl`, else `main`); the join descriptor's `zone=` is empty while the flag is off,
  so prod behavior is byte-identical to today. Compiles clean.
- **WSL workers** — `CLOUD_GAME_WORKER_NETWORK_ZONE=main` added to `docker-compose.gpu.yml` (applied on next `up -d`;
  harmless now because the site still sends an empty zone, which `Worker.In` wildcard-matches).
- **GL worker config** — `docker/arcade/config.worker-gl.yaml` (GL cores + NVENC/Opus-FEC encoder, Windows paths).
- **Run model** — `scripts/run-arcade-glworker.ps1` (env + MSYS2 PATH + restart loop) and
  `scripts/register-arcade-glworker-task.ps1` (interactive logon task).
- **BIOS staged** — `D:\ArcadeStorage\bios\dc\` now holds dc_boot/dc_flash/naomi.zip/awbios.zip (flat copies kept);
  PPSSPP assets copied to `D:\ArcadeStorage\bios\PPSSPP\` (116 files).

**Remaining (needs elevation / interactive GPU session — hand-run on Ziggy):**
1. **Install toolchain (elevated):** `! winget install --id MSYS2.MSYS2 -e` and `! winget install --id GoLang.Go -e`
   (a non-interactive shell can't elevate; run these yourself). Then in a **UCRT64** shell:
   `pacman -Sy --needed git make mingw-w64-ucrt-x86_64-{gcc,pkgconf,gstreamer,gst-plugins-base,gst-plugins-good,gst-plugins-bad}`
   → sanity: `gst-inspect-1.0 nvcodec` (absent ⇒ set the gl config's `encoder.video.codec: vp8`).
2. **Build:** copy the pinned tree clean (`git clone -c core.autocrlf=false … && git checkout 13852a7`), apply patches
   **0001 0002 0004 0005** (skip 0003), then in the UCRT64 shell `go build -o bin/worker.exe ./cmd/worker`.
3. **Stage config:** put `config.worker-gl.yaml` as `config.yaml` in `D:\ArcadeStorage\worker-gl\` (the ConfDir the
   run script points at).
4. **Network:** router UDP-forward **8446 → Ziggy** + a Windows Defender inbound UDP allow for `worker.exe`.
5. **Start + verify (test-roms, PROD origin):** `scripts/register-arcade-glworker-task.ps1` → tail
   `D:\ArcadeStorage\logs\glworker.log` for a **zone:gl** registration + NVIDIA GL 4.x context. A PSP and a DC
   title must reach `Playing` and hold (this is exactly where WSLg died).
6. **Coordinated zoning cutover (only after 5 passes — avoids the empty-zone trap):**
   a. `docker compose -f docker-compose.gpu.yml up -d` (recreate WSL workers so `zone=main` takes effect — verify the
      `/mnt/d` paths first per the compose gotcha); b. set `ArcadeZoningEnabled=true` in the prod appsettings secret
   (movietheater-secret skill) + restart the pod; c. confirm 2D rooms still land (`main`) and PSP/DC now route to the
   gl worker. Do (a)+(b) close together — while workers are `main` but the site still sends empty, 2D is fine; the
   only bad window is site-sends-`main` before workers are `main`.
7. **Flagship:** Dreamcast 4-player two-browser (`arcade-mp.mjs` against a DC title).

## Risks / open items
- Keep the Windows build pinned to the SAME SHA + patch set as the image; rebuild both together
  at the WS-F rebase (upstream's post-GStreamer master will change `pkg/worker/media` under 0004).
- If B2 wins and appetite grows, nothing stops MORE Windows workers (8447, 8448…) — or, at the
  rebase, revisiting whether the whole fleet moves native and WSL retires. Decide on evidence
  from the A/B, not now.
