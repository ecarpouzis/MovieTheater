# Arcade Roadmap — the future of /arcade (research + plan, 2026-07-04)

**Status:** research synthesis + recommended plan. Written after a 5-track research pass
(CloudRetro upstream health, browser-streaming landscape, client-side WASM emulation, WSL2/Windows
GPU hosting, emulator/save/EmuDeck facts). Companion docs: `arcade-plan.md` (architecture),
`arcade-3d-cores-wslg.md` (PSP/DC GL blocker scope), `arcade-saves-plan.md` (save vault S0–S4),
`emulatorjs-plan.md` (solo lane), the `arcade` skill.

---

## 1. Goals (Eric, 2026-07-04)

- **G1 — Browser-first stays.** Games-in-browser is the product; multiplayer is the crown jewel.
  Dreamcast multiplayer in browser is explicitly desired. Systems that truly can't work in-browser
  may peel off to other solutions.
- **G2 — Bang-for-buck on the stack.** Concerns: our CloudRetro patch burden vs better-maintained
  alternatives; wrong aspect ratio in fullscreen; per-ROM fixes; RetroAchievements appeal.
- **G3 — Fix the spotty performance.** Bomberman 64 adventure mode unplayable (2-min intro
  cinematic renders as flat color planes — gliden64 HLE limit); mild same-host UDP hairpin loss
  (NACKs climb on every N64 title); audio-clock drift growing the buffer; video recovers via
  retransmit (0 freezes), audio doesn't → occasional audio skips on-network (off-network was clean).
- **G4 — EmuDeck save continuity.** Start a game on the Deck, continue in the browser, and back.
- **G5 — Later:** per-player/system controller remapping. Not urgent.

Also desired if hardware allows: PSP + Dreamcast now; PS2 / Wii / Switch eventually
(hardware is ample: i7-13700K 16C/24T, 64 GB, RTX 4070 Ti — the constraint is platform plumbing,
not horsepower).

## 2. Research verdicts (July 2026)

### 2.1 CloudRetro upstream — alive; do NOT replace; rebase later (CONFIRMED)

- `giongto35/cloud-game` is actively maintained by a **single** de-facto maintainer
  (sergystepanov, 786 commits vs owner's 122). ~100 commits May–Jul 2026 replaced the whole media
  stack with **GStreamer** (PR #504, merged 2026-06-28): x264 deleted, hand-rolled encoders gone,
  any GStreamer codec becomes config — i.e. **our NVENC patches 0003/0004 have an upstream
  equivalent now**. No tagged release since v2.6.1 (2021); bus factor 1; no other production users.
- **No OSS replacement exists** for libretro-native multiplayer over WebRTC. Alternatives
  (neko, selkies) stream a desktop and lose per-seat input arbitration, save semantics, and our
  multi-disc/JIT work. Wolf is Moonlight-protocol (native clients), not browser.
- Upstream already fixed things we still suffer: **PSX fullscreen aspect ratio (2024-03-21)** —
  server pushes `{w,h,aspect,scale,flip,rot}`, client recomputes on fullscreen; audio pipeline
  re-timed (Speex resampler 2025-12, GStreamer clocking 2026-06). No explicit "drift" fix, but the
  code path is entirely new.
- GL cores: upstream is still **X11+GLX on Linux — no EGL, no Vulkan**. Our WSLg blocker persists
  on latest master. BUT: the worker is a supported **native Windows build target** (MSYS2
  GStreamer, WGL headless-capable) — real NVIDIA GL without WSLg.
- Audio robustness facts:
  - Pion's default SDP already negotiates `useinbandfec=1`… but **Opus in-band FEC is a silent
    no-op in our encoder config**: the inherited default `restricted-lowdelay` (CELT-only) + 5 ms
    frames disable LBRR. Fix = `OPUS_APPLICATION_AUDIO` + `OPUS_SET_INBAND_FEC(1)` +
    `OPUS_SET_PACKET_LOSS_PERC(~15)` + ≥10 ms frames. **CORRECTION (source-verified at `13852a7`):
    this is config-only NOW, not a patch** — CloudRetro's GStreamer audio path (`buildAudioPipeline`)
    already threads `encoder.list.opus.params` to `opusenc`. Landed via config (WS-A.2).
  - **Root-cause lever for the hairpin loss:** a known Pion footgun (pion/webrtc #1915: "UDPMux
    causes massive packet loss", 50–70 % observed on local nets). **CORRECTION (source-verified at
    `13852a7`): CloudRetro DOES call `SetReadBuffer`/`SetWriteBuffer(16 MiB)`** on the mux socket
    (`pkg/network/socket/socket.go`) — the footgun is already coded around. The remaining lever is
    purely the kernel clamp: raise `net.core.rmem_max`/`wmem_max` in the WSL2 distro (LiveKit ships
    25165824) so that 16 MiB request isn't capped to the ~208 KiB default. No fork patch. Landed via
    a committed sysctl drop-in (WS-A.1). NACK-for-audio isn't browser-viable; RED is Chrome-only with
    no Pion encoder — FEC + buffers is the whole play.
- **RetroAchievements:** zero integration upstream. rcheevos (MIT, active) is designed for
  embedding (`rc_client` + `rc_libretro.c` memory-map helpers); moderate cgo effort in
  CloudRetro's nanoarch if we ever want it. Product wrinkle: whose account in a shared room
  (answer: creator's), and hardcore mode conflicts with savestates/seeding (softcore only).
- **Rebase timing: not now.** Master is mid-rewrite. Plan a deliberate rebase **~Q4 2026** once
  the GStreamer sprint settles: drop 0003/0004 (upstream GStreamer), keep 0001 scan-on-miss +
  0002 udp4 mux + 0005 disc control (no upstream counterpart), re-verify `vfr` (upstream now
  defaults true; we forced false for judder) and the aspect/audio behavior.

### 2.2 Client-side WASM (EmulatorJS lane) — solo 2D yes; PSP/DC no (CONFIRMED)

- EmulatorJS alive (stable 4.2.3 from 2025-07; 4.3.0-pre 2026-05 adds WebRTC netplay, better
  PPSSPP HW rendering, ES6 rework). Single-maintainer-ish.
- **Dreamcast client-side is not viable** — no official core; community flycast-wasm runs
  single-digit FPS (no dynarec in WASM; experimental wasm-JIT branch unreleased). **PSP
  client-side is marginal** — IR interpreter only, stutter reports on strong desktops, needs
  threads → COOP/COEP cross-origin isolation. Both belong on the server lane.
- **RetroAchievements: does not exist in EmulatorJS** (maintainer-labeled "Not Planned").
- **Netplay: nightly-only, unproven, ≤4 players, needs its own Node server — ignore.** CloudRetro
  already does multiplayer better.
- Saves: EJS cores are RetroArch cores; **.srm round-trips byte-compatibly with desktop
  RetroArch** (explicit compat target since 4.2.1) and stable-channel hooks exist for backend
  save sync (`saveSaveFiles` event + save interval; RomM does exactly this in production).
  Validates the save-vault design.
- Verdict: `emulatorjs-plan.md` stands as written — 2D era (+PS1 as a later option; it's
  full-speed in browser but needs client BIOS), pinned 4.2.3, no threads/COOP-COEP. PSP/DC solo
  via EJS: rejected.

### 2.3 Browser-streaming / desktop-streaming landscape (research agent died mid-task; filled
from model knowledge ~early 2026 — items marked ⚠VERIFY need a fresh check only if that path
is actually pursued)

**Headline: nothing off-the-shelf delivers "N browser users, each with their own gamepad, one
shared session." CloudRetro remains the only browser-multiplayer engine we can run.**

- **neko** (m1k1o, ~21k★, v3.x 2025–26, active — activity confirmed by §2.1 research): multi-user
  browser control of one containerized desktop over WebRTC. Gamepad/joystick passthrough has been
  a years-open request and was **not a shipped feature** as of early 2026 ⚠VERIFY. Even with pads,
  per-seat arbitration (who is P1/P2) would be DIY — the thing CloudRetro gives us for free.
- **Selkies 2.x** (active): high-quality 1:1 WebRTC remote desktop; uinput gamepads exist but for
  the single controlling user; not a multi-user-input system.
- **Wolf** (games-on-whales): multi-user sessions with per-client virtual pads — but **Moonlight
  protocol (native apps only, no browser client)** and requires native Linux NVIDIA drivers,
  which this box cannot provide (wolf#129; §2.4 verdict 4). Only relevant if a dedicated Linux
  GPU box ever exists.
- **Parsec** (Unity-owned, proprietary): hosting is Windows/macOS only; its whole pitch is couch
  co-op — guests get server-side virtual gamepads via the native client. A web client
  (web.parsec.app) exists; **whether a web guest can use a gamepad is ⚠VERIFY** (an afternoon
  test) — this is the one shortcut to "browser guest plays a PS2/Dolphin game on the Windows
  host" if it works. Accounts required; relay fallback; ToS/free-tier fine for personal use.
- **Moonlight in a browser**: nothing maintained exists (old moonlight-chrome is dead) ⚠VERIFY.
- **Nestri**: early OSS browser cloud gaming; single-user focus, immature as of early 2026.

Conclusion → the §3 lane split stands: browser MP stays CloudRetro; the heavy lane starts
native-client (Moonlight), with Parsec-web as the only near-term browser-reach experiment.

### 2.4 WSL2 / Windows GPU hosting facts (CONFIRMED — incl. empirical tests run on Ziggy 2026-07-04)

- **NVENC in WSL2: officially supported since driver 570+/Video Codec SDK 13.0 (2025), empirically
  confirmed on this box** (driver 596.21: `h264_nvenc` 5.2× realtime, `hevc_nvenc`, NVDEC all work
  via `/usr/lib/wsl/lib`). Matches what our stack already does. Gotcha: ffmpeg/GStreamer builds
  must target NVENC API ≤ the driver's (a "requires 13.1, found 13.0" class error means the build
  is too new for the driver).
- **Headless GPU GL in WSL2: still impossible.** dxgkrnl exposes `/dev/dxg` only, never a DRM
  render node (`/dev/dri` absent — verified locally), so mesa's surfaceless/GBM EGL paths cannot
  init d3d12 (wslg#1302 open, unanswered). GPU GL (a real **GL 4.6** since Mesa 24) is reachable
  **only through WSLg's presentation paths** — GLX via Xwayland or EGL-on-Wayland. Containers
  additionally need `/dev/dxg` + `/usr/lib/wsl` + **the WSLg sockets** mounted (we already do).
- **Vulkan under WSL2 (mesa "dozen"): dead end.** Stuck at Vulkan 1.0 100% / 1.2 75% / 1.3 17%,
  development stalled, not distro-shipped (must self-build mesa), zero emulator success reports.
  **PCSX2 / Dolphin / Switch-class emulation under WSL2 is not viable — and ParaLLEl-RDP (the
  N64 LLE renderer that would fix Bomberman 64 cinematics) is equally out of reach there.**
- **No real-driver Linux VM is possible on this box.** DDA (PCIe passthrough) is Server-SKU only;
  client GPU-P for Linux guests reproduces exactly the WSL `/dev/dxg` ceiling. The only paths to
  native Linux NVIDIA are a dedicated Linux box or dual-boot.
- **Native Windows hosting is healthy:** Sunshine active (calver, 2026-05 release); the **Apollo**
  fork bundles the SudoVDA virtual display + headless mode + per-client resolution. 4070 Ti has
  **2× NVENC engines**, AV1+HEVC; consumer concurrent-session cap now 8→**12** (driver 591.44+).
  One active session per Sunshine/Apollo instance; run multiple instances for concurrent
  independent sessions. **Moonlight has no browser client — native apps only** (Steam Deck's is
  excellent).
- **Networking:** mirrored mode + `hostAddressLoopback` are **already deployed** on Ziggy — the
  NAT-hairpin era is over, so the residual same-host loss is not NAT architecture. Chase it at
  the socket layer (§2.1 UDPMux buffers), the Hyper-V firewall
  (`Set-NetFirewallHyperVVMSetting -DefaultInboundAction Allow`), and known mirrored-mode edge
  cases (container TCP stalls moby#48201; multicast broken). `netsh portproxy` remains TCP-only —
  never a WebRTC option (long-standing house rule, reconfirmed).

### 2.5 Emulators / saves / EmuDeck facts (research agent died mid-task; filled from model
knowledge ~early 2026 — ⚠VERIFY items at build time, none block the plan's shape)

- **RetroAchievements is native in the standalone emulators** (this is the cheap RA play — no
  browser stack has it): PPSSPP (≥1.16), DuckStation, PCSX2, Dolphin (mid-2024), flycast, melonDS
  all integrate rcheevos with per-user logins ⚠VERIFY exact current state per emulator at pilot.
  Hardcore mode disables savestates → **incompatible with our state seeding; run softcore**.
- **Save-format granularity** (drives the vault's per-game model):
  - RetroArch-family `.srm` = raw SRAM, per-game, byte-compatible across CloudRetro, EmulatorJS,
    desktop RetroArch — already the vault's canonical artifact (arcade-saves-plan §1).
  - PPSSPP: per-game `SAVEDATA/<GameId>/` folders in the memstick tree; same layout on Deck
    (standalone) and libretro core ⚠VERIFY cross-compat once.
  - PCSX2: default memcards are two monolithic 8 MB `.ps2` files — **enable "folder memory
    cards"** (per-game subfolders) for vault granularity.
  - Dolphin: GC = per-game `.gci` (use GCI-folders mode); Wii = NAND per-title dirs (syncable
    per-title, fiddlier).
  - flycast: VMUs are monolithic files; the libretro core has a per-game-VMU option ⚠VERIFY;
    standalone shares VMU A1 — sync whole-VMU when per-game isn't available.
  - Switch forks: per-title save dirs under the emulated NAND; portable within a fork family.
- **Switch emulator landscape post-takedowns** is fork soup (Ryujinx continuations, yuzu forks:
  Eden/Citron etc.) — alive-ness churns quarterly; **pick at pilot time, not now** ⚠VERIFY.
- **EmuDeck CloudSync**: rclone-based, supports major clouds + self-hosted WebDAV/Nextcloud;
  syncs around emulator launch/exit via their launcher wrappers; conflict handling is primitive
  (timestamps) ⚠VERIFY current mechanics when S4 starts. Community alternative: Syncthing on
  `Emulation/saves/` (pitfall: sync-while-emulator-running corrupts — use versioning + only sync
  at rest). Ziggy can serve a WebDAV target via the existing Caddy if we want EmuDeck's own
  CloudSync to point at us.
- **Per-user isolation on a shared host** (heavy lane): Dolphin `-u <userdir>`, PCSX2 portable
  mode/`-cfgpath`, PPSSPP `--memstick`-style redirection, RetroArch `--config`/appendconfig —
  exact flags ⚠VERIFY at build; the pattern (one profile dir per site-user, seeded/harvested by
  the same vault) is safe to design against.

## 3. Recommended architecture: one library, three lanes, one save spine

```
                    ┌─ Lane 1: CloudRetro (patched fork) ── browser MULTIPLAYER
                    │    2D era + N64 + PSX today; + PSP/DC/Naomi/AW after the GL unlock (WS-B)
/arcade catalog ────┼─ Lane 2: EmulatorJS ───────────────── browser SOLO, zero Ziggy cost, mobile
(ArcadeGame rows,   │    2D era per emulatorjs-plan.md (PS1 later); N64/PS1 solo = 1-player rooms
 per-game routing)  │
                    └─ Lane 3: Heavy streamed lane ──────── PS2 / GC / Wii / Switch (+PSP/DC fallback)
                         standalone emulators + GPU streaming; browser story per §2.3 findings;
                         Steam Deck & desktops can always use native Moonlight regardless
                                │
              Save vault spine (arcade-saves-plan.md): user+game keyed SRAM/memcards
              seeds/harvests every lane; EmuDeck bridge (S4) attaches here
```

Principles:
- **CloudRetro is not being replaced** — it's the only game in town for browser MP and it's
  cheaper to fix (buffers+FEC+aspect are small) than to migrate. Keep the patch set minimal and
  isolated; freeze-exit stays viable (bus factor 1 upstream).
- **Per-game lane routing lives in the catalog** (`ArcadeGame.SoloEngine` already planned;
  a `Lane`/`StreamEngine` column generalizes it later). One catalog, one age gate, one UI.
- **Saves are the product glue.** Whatever lanes exist, the user's SRAM/memcard is one artifact
  in one vault; states stay lane-scoped (never portable). EmuDeck sync attaches to the vault,
  not to any lane.

## 4. Workstreams

### WS-A — Transport & polish on the current stack (do first; small, high value)
1. **UDP socket buffers (root cause) — ✅ LANDED (config, needs Ziggy install + verify).**
   *Correction from source (pinned SHA `13852a7`):* this is **NOT a fork patch** — CloudRetro
   **already** calls `SetReadBuffer/SetWriteBuffer(16 MiB)` on the mux socket
   (`pkg/network/socket/socket.go`, and the `_ =` discards the error). The real bug is that the
   kernel **silently clamps** that request to `net.core.rmem_max` (~208 KiB distro default), so the
   mux runs on a 208 KiB buffer and inbound RTP bursts overrun it. Fix is therefore **sysctl-only**:
   committed `docker/arcade/99-arcade-udp-buffers.conf` (rmem_max/wmem_max = 25165824) → install into
   the distro's `/etc/sysctl.d/` (systemd re-applies each boot; workers are host-network so the distro
   sysctl governs). Still check the Hyper-V firewall (`Get/Set-NetFirewallHyperVVMSetting`,
   `DefaultInboundAction`) — mirrored+hostAddressLoopback are already deployed, so residual loss is
   socket/firewall-layer, not NAT. Verify with test-roms: NACK count on a 10-min N64 session
   before/after. Clean A/B control: a **WS-B gl-zone Windows worker** binds a native Windows socket
   with **no mirrored-relay hop** (`docs/arcade-windows-worker.md`) — if its N64/audio loss is ~0
   while a WSL worker still shows loss post-sysctl, the residue is the relay, not the buffer.
2. **Opus in-band FEC (masking) — ✅ LANDED (config).**
   *Correction from source:* also **NOT a fork patch** — CloudRetro's GStreamer audio path already
   threads encoder `Params` (`buildAudioPipeline` in `pkg/worker/media/gstreamer.go`) and reads
   `frame-size` back to time the RTP frames, so this is **config-only**. The embedded default
   (`pkg/config/config.yaml`) is `audio-type=restricted-lowdelay … frame-size=5`, which we inherited
   by setting no `opus` entry — that's why FEC was a silent no-op (CELT-only + sub-10 ms kills LBRR).
   Committed `docker/arcade/config.yaml` `encoder.list.opus` override:
   `audio-type=generic inband-fec=true packet-loss-percentage=15 frame-size=10 complexity=8 bitrate=96000`.
   **Verified live (test-roms vs prod, 007-GoldenEye N64, 52 s same-host):** opusenc accepted the
   params (audio healthy) and `fecPacketsReceived` climbs ~1:1 with packets received (~4686/5001) —
   i.e. LBRR is now embedded in every Opus packet, where before it was **0**. Audio loss 0/5001,
   2 concealment events total (startup). FEC is genuinely on the wire.
3. **Fullscreen aspect fix — ✅ LANDED (frontend; deploy to go live).** Implemented client-only in
   our shim, no fork touch: `ArcadeRoomPage.js` now renders a two-box player — an outer black
   surface (the fullscreen target) centering an **inner box that always carries the per-system
   display aspect**, with `object-fit:fill` staying INSIDE it. A `fullscreenchange` listener toggles
   the layout; in fullscreen the inner box is sized to the largest aspect-correct rectangle that fits
   the screen (`width: min(100%, calc(100vh * ar))`) → letterbox bars instead of the old wide smear.
   Windowed rendering is visually unchanged. UI build passes.
4. **Audio jitter-buffer A/B — ✅ DECIDED: keep `jitterBufferTarget=0` (no change).** The live verify
   settles it: actual audio playout delay is a *flat* 42 ms with **no lip-sync drift** (video tracks
   wallclock 1:1, `media-playout` steady), loss 0, concealment ~0 at target 0. The A/B's premise was
   masking *loss-induced* skips; FEC (item 2) now handles that at the source without adding steady
   latency. Raising the audio target to 40–60 ms would regress a clean stream on spec. **Reopen only
   if** real cross-network multiplayer shows `concealmentEvents` / `removedSamplesForAcceleration`
   climbing under sustained loss — then a small audio-only target is the lever, video target stays 0.
5. **Bomberman 64 adventure mode:** accept as documented gliden64-HLE limitation for now
   (every FB option already tried; angrylion panics CloudRetro). The real fix is LLE RDP
   (ParaLLEl = Vulkan) — parked under WS-B/WS-E platform work. Don't re-chase per-game config.

### WS-B — PSP / Dreamcast unlock (the "DC multiplayer in browser" goal)
Two competing spikes; run cheap-first, pick a winner:
1. **Spike B1 — EGL context patch under WSLg** (`arcade-3d-cores-wslg.md` option A, sharpened by
   §2.4): step 0 is `eglinfo` **inside a worker container**, probing the **X11 and Wayland
   platforms** (the WSLg sockets are already mounted). Surfaceless/headless EGL *will* fail —
   that's expected (no `/dev/dri`) and is NOT the verdict; only the display-attached platforms
   matter. If EGL-on-X11 or EGL-on-Wayland exposes **desktop GL ≥3.3 core** on d3d12 → patch the
   fork's RGFW-ish graphics layer to create contexts via EGL for GL cores (new patch
   `0006-egl-context`), rebuild, test flycast/ppsspp. If it only exposes GLES → try the cores'
   GLES modes before giving up. (libgomp1 already staged in Dockerfile.gpu; mupen proves the
   d3d12 display path is GPU-capable.)
2. **Spike B2 — Windows-native worker** (upstream-supported WGL build, headless-capable) —
   **now fully scoped from source: `docs/arcade-windows-worker.md`** (build recipe, zoning
   changes, run model, acceptance). Build the worker under MSYS2 on Ziggy, Windows libretro
   core DLLs from the libretro buildbot. Gives
   **native NVIDIA GL 4.6** (no WSLg constraints at all) + native NVENC + WSL-relay-free UDP for
   those rooms. Operational cost: a non-docker worker process (NSSM/Task Scheduler service),
   separate core/ROM paths, our patches must build under MSYS2.
   **Routing trap (must solve before mixing worker types):** the coordinator hands rooms to any
   free worker — a PSP room landing on a WSL 2D worker just fails. Options: (a) verify
   CloudRetro's `zone` mechanism isolates pools (our join descriptor already carries `zone=`,
   empty today — thread it from `ArcadeGame.System` through token→gateway→WS query); or (b) run
   a **second coordinator** (different port) for the GL pool and have the gateway pick
   `CoordinatorBaseUrl` by system. (b) is dumber and safer.
3. Whichever lands: enable psp/dc/naomi/atomiswave rows, per-title test-roms pass, then
   **Dreamcast 4-player in browser** (Power Stone 2 / MvC2 class) is the flagship demo.
4. **N64 LLE (Bomberman 64 cinematics) does NOT belong here:** ParaLLEl-RDP is Vulkan;
   CloudRetro has no Vulkan support on any OS and dozen is dead (§2.4) — so no CloudRetro
   configuration can ever fix it. It's a WS-E item (RetroArch+Vulkan on the native lane).

### WS-C — EmulatorJS solo lane (as designed)
Execute `emulatorjs-plan.md` Phases A–C unchanged (2D era, pinned 4.2.3, iframe-srcdoc,
token-in-path URL shape). Add nothing for PSP/DC (research says no). Wire its save hooks into
the vault when WS-D lands (the plan's §8 hooks + stable-channel `saveSaveFiles` event).

### WS-D — Save vault + EmuDeck bridge (the spine; G4)
Execute `arcade-saves-plan.md` S0→S3 (verify deterministic ids → gateway store/seed/harvest →
site wiring → UI). Then S4 (EmuDeck) informed by §2.5 findings — likely shape: the vault exposes
a per-user RetroArch-layout saves view (SRAM/memcards only), synced to the Deck via
rclone/Syncthing or EmuDeck CloudSync's own transport; name-mapping keyed on
`(system, rom basename/hash)` with a manual-pin UI for mismatched Deck filenames.
All bulk sync jobs: chunked, resumable, guarded, dry-run first (house rule).

### WS-E — Heavy lane pilot (PS2 / GC / Wii / Switch) — **shape now decided by §2.3/§2.4**
> **Execution plan written 2026-07-10: `docs/arcade-heavy-lane-plan.md`** — scope updated there
> (PS2/GC now live in-browser via CloudRetro; heavy lane = Switch/PS3/PS4/WiiU/X360 via Apollo +
> Moonlight, generic per-app descriptor layer, save-vault attach). That doc supersedes the sketch
> below where they differ.
**Pilot = Apollo (or Sunshine) natively on Ziggy's Windows + Moonlight clients.** Rationale:
no browser multi-gamepad streamer exists (§2.3); WSL2 can't run Vulkan emulators and no
real-driver Linux VM is possible on this box (§2.4); native Windows gets full GPU + 2× NVENC.
- Emulators: PCSX2, Dolphin, PPSSPP (full-speed alternative to the browser lanes), flycast
  standalone, a Switch fork chosen at pilot time (§2.5 ⚠VERIFY). All render natively (Vulkan/D3D);
  **ParaLLEl-RDP N64 via RetroArch+Vulkan lives here too — the only real fix for Bomberman 64's
  adventure-mode cinematics** (gliden64 HLE limit, unfixable in CloudRetro).
- Clients: **Steam Deck = Moonlight, first-class** (better than any browser); desktops install
  Moonlight. Browser reach for heavy titles = the Parsec-web gamepad experiment (§2.3 ⚠VERIFY)
  or wait for a future Linux box; do not block the pilot on it.
- Multi-user: one Apollo/Sunshine instance = one active session; run 1–2 extra instances if two
  heavy sessions must coexist; Moonlight guests supply multiple controllers into one session for
  couch co-op (that's the Moonlight/host feature set, not our code).
- Site integration: `/arcade` cards for heavy titles show "Play via Moonlight" (pairing
  instructions + launch deep-link where possible) — the catalog/age-gate stays ours even when
  the transport isn't. Saves integrate via WS-D per-user profile seeding/harvest on Ziggy.
- **RetroAchievements comes free here** (native in PPSSPP/PCSX2/Dolphin/DuckStation/flycast;
  per-user emu profiles carry per-user RA logins; softcore only — §2.5).

### WS-F — Rebase window (~Q4 2026)
One planned effort after upstream's GStreamer churn settles: re-pin SHA, drop 0003/0004,
re-express 0001/0002/0005 (+0006 if B1 won), adopt upstream aspect/audio behavior, re-verify
vfr/judder, NVENC-vs-config, and the keyframe/late-join story. Exit criterion for staying
attached: patch count stays ≤4 and rebases stay ≤a day of work; otherwise freeze the fork
deliberately.

### WS-G — RetroAchievements & controller mapping (later)
- RA: ships with the heavy lane's standalone emulators (per-user login via per-user emu
  profiles). CloudRetro-lane rcheevos patch = optional future (softcore only, creator's account).
  EmulatorJS: not available, period.
- Controller remapping (G5): client-side per-user/system overrides of the `PROFILES` table in
  `cloudRetroClient.js` (localStorage first, DB later); EJS has its own remap UI already;
  heavy lane inherits emulator-native remapping via per-user profiles.

## 5. Sequencing

1. **WS-A** (days; fixes every N64 session today) →
2. **WS-B spikes** (a weekend each; unlocks PSP + the DC-MP flagship) →
3. **WS-D S0–S3** (save vault — prerequisite for G4 and enriches every lane) →
4. **WS-C** (EmulatorJS solo; independent, can interleave) →
5. **WS-E pilot** (heavy lane, shape per research) →
6. **WS-D S4** (EmuDeck bridge) →
7. **WS-F rebase** (~Q4 2026) →
8. **WS-G** (RA + remapping polish).

## 6. Decision log / open decisions

- **DECIDED (research):** stay on CloudRetro for browser MP; EmulatorJS for solo 2D; no
  client-side PSP/DC; RA via standalone emulators, not browser stacks.
- **OPEN D1:** PSP/DC unlock path — EGL patch (B1) vs Windows-native worker (B2). Decide by spike.
- **DECIDED D2 (by research):** heavy-lane transport = Moonlight-native (Apollo/Sunshine on
  Windows). Browser reach for heavy titles is a bounded experiment (Parsec-web gamepad test),
  not a blocker.
- **OPEN D3:** rebase go/no-go in Q4 2026 (upstream stability check).
- **OPEN D4:** hardware — nothing needed now. A dedicated Linux GPU box becomes interesting only
  for: Wolf-class multi-user heavy streaming, or browser-MP of heavy systems (custom WebRTC on
  native Linux GL/Vulkan). Revisit after WS-E proves demand.

---

## 7. Appendix — implementer traps (read before building; these WILL bite otherwise)

**CloudRetro / fork (Lane 1):**
- The backend can NEVER create CloudRetro rooms (HTTP surface is `/`,`/ws`,`/wso` only); the
  creator's browser creates the room and reports the id via Bind. One worker = one room.
  Never expose the coordinator (no auth). Never iframe the stock client (hardcoded `/ws`).
- ICE must advertise the LAN/public IP — `127.0.0.1` can never work; same-host play additionally
  needs `.wslconfig hostAddressLoopback=true` (both already deployed).
- **Opus FEC is a no-op unless you ALSO leave CELT-only mode**: flipping `inband-fec` while
  keeping `restricted-lowdelay`/5 ms frames changes nothing (LBRR exists only in SILK/hybrid,
  ≥10 ms frames). Required together: application AUDIO/generic + FEC on + loss-perc ~15 +
  10–20 ms frames. Pion's default SDP already offers `useinbandfec=1` — the encoder is the gap.
- Audio NACK/RTX is not browser-viable; RED has no Pion encoder — don't chase either.
- The socket-buffer patch goes where the **single-port UDP mux** socket is created (search
  `ListenUDP`/singleport in `pkg/network`): `SetReadBuffer`/`SetWriteBuffer` BEFORE wrapping in
  the ICE UDP mux. Workers are host-network → set `net.core.rmem_max/wmem_max` in the
  Ubuntu-24.04 distro itself.
- The fullscreen-aspect fix is **client-only in OUR shim** (`ArcadeRoomPage.js` /
  `cloudRetroClient.js`) — do not patch the fork for it. Fullscreen the wrapper, center an inner
  box sized to the per-system DISPLAY aspect, keep `object-fit:fill` on the video INSIDE that box
  (`contain` is wrong: stream pixels are non-square, e.g. PSX 512×240).
- Keep `receiver.jitterBufferTarget = 0` until FEC lands (it cures the lip-sync drift); only then
  A/B a small audio-only target. Do not raise the video target — that re-introduces the drift.
- `coreAspectRatio: true` on GL cores is load-bearing (carries `Flip`; without it GL renders
  upside down). angrylion hard-panics — never enable. `vfr false` stays until the Q4 rebase.
- Never judge stream smoothness in headless Chrome (fabricated judder); use the test-roms skill
  against the PROD origin (gateway rejects localhost origins).
- Rebase (WS-F): re-pin a post-GStreamer SHA; DROP 0003/0004 (upstream GStreamer replaces them —
  NVENC becomes config, e.g. `nvh264enc`, verify caps/NV12 bridge); KEEP 0001 scan-on-miss,
  0002 udp4 mux (mirrored WSL relays inbound UDP only to AF_INET sockets), 0005 disc control
  (no upstream equivalent). Clone with `core.autocrlf=false` (CRLF breaks image shell scripts).
- Saves (WS-D): `saveCompression: false`; NEVER configure CloudRetro's own S3 `storage.provider`
  (it overwrites seeded saves at boot); deterministic id MUST be `<prefix>___<gameName>` (the
  `___` + valid game suffix is mandatory or the id is rejected); multiplayer harvests to the
  **creator's** vault; `.dat` states never leave the online lane — only `.srm` syncs.

**EmulatorJS (Lane 2):** pin 4.2.3; token as a path segment with a **stable filename last**
(SRAM keys off the basename / zip inner name); `EJS_gameName` + `EJS_gameID` always set;
iframe-`srcdoc` mount with `callEvent("exit")` teardown (no destroy method exists); no
threads/COOP-COEP in v1 (rules out PSP client-side — don't try); netplay stays off (nightly-only
upstream, unproven); gateway ROM route must answer HEAD with Content-Length.

**WS-B spikes:** eglinfo's surfaceless failure is expected and meaningless — only the X11/Wayland
platform results decide B1. Don't mix Windows-native and WSL workers on one coordinator until
routing is solved (zone verified, or second coordinator per pool) — the coordinator will
happily hand a PSP room to a worker that can't run it.

**House rules that keep applying:** shared prod/dev DB (migrations: generate → read SQL → apply);
stage explicit paths, never `git add -A`; never touch L:\ NAS destructively; bulk jobs chunked +
resumable + dry-run; new antd components need their style import in `src/ui/src/index.js`.
