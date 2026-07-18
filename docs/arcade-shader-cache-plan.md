# Arcade shader-cache persistence plan — GL + Vulkan

**Goal:** every game staged on D: boots with a warm shader/pipeline cache on **both** renderers
(the play-button pill `fe1f9c7` lets a user force Vulkan or GL per launch), on **both** workers,
surviving room closes and worker recycles, invalidating only when the core build changes.

Written 2026-07-18 after a full audit (git history + fork source + Dolphin source + live logs +
on-disk cache state). Everything below is measured, not assumed — evidence is cited inline.

---

## 1. Measured current state (2026-07-18)

Caches are **per ConfDir** (`D:\ArcadeStorage\worker-gl[-2]\`) and **per-backend keyed by
filename**, so GL and Vulkan caches coexist without conflict — the per-launch renderer pill does
not need renderer-scoped storage work.

| System | Core | GL cache | Vulkan cache | Persists today? |
|---|---|---|---|---|
| gc / wii | dolphin_custom | `…legacy_save/User/Cache/Shaders/OpenGL-*` + `<GameID>.uidcache` | same dir, `Vulkan-*` + shared `<GameID>.uidcache` | **YES both arms** — but see the Reload bug (§3) |
| ps2 | LRPS2 (from source) | `…libretro/system/pcsx2/cache/gl_programs.{bin,idx}` | none found (paraLLEl-GS) | GL yes; **Vk no** (gap G3) |
| psp | PPSSPP stock | `…legacy_save/PSP/SYSTEM/CACHE/<ID>.glshadercache` | `<ID>.vkshadercache` | **YES both arms** (self-written mid-session) |
| n64 | mupen + paraLLEl-RDP | — | no core disk cache | driver cache only (G5) |
| ps1 | Beetle PSX HW (Vk) | — | no core disk cache | driver cache only; tiny shader set |
| dc / naomi / atomiswave | flycast custom | no core disk cache | no core disk cache | driver cache only (G5) |
| 2D cores | software render | n/a | n/a | n/a |

**How persistence works (the load-bearing mechanism):** the worker's teardown order for LibCo
cores is `retro_unload_game` → `retro_deinit` → *then* video deinit
(`nanoarch.go Shutdown()`, ~line 563). Dolphin's `retro_unload_game` (`Boot.cpp:644`) calls
`g_video_backend->Shutdown()` itself whenever `context_destroy` never ran — which flushes
`ShaderCache` (uidcache + pipeline caches) **while the GL/Vk context is still alive**. PCSX2
writes `gl_programs` in `retro_unload_game` too (proven by cache growth 71→98→101 across clean
closes, commit `8225efa` note). PPSSPP appends to its cache during gameplay.

**Consequence: cache persistence does NOT require the core's `context_destroy` to run.**

**Build-identity isolation already exists:** `coreCache: {dir, purge}` + `.core-build` stamp
(`frontend.go purgeForeignCoreCache`) drops a cache written by a different core build. Currently
armed for gc/wii only. Log proof line: `core cache User/Cache owned by this build (7ecde731…)`.

---

## 2. `skip_hw_context_destroy` audit — verdict: KEEP on all seven hack lists

This audit ran because we nearly removed the hack from gc/wii's Vulkan arm to "fix" cache
persistence. Recorded here so nobody retries a dead end:

| System | Why the hack exists | Removal ever tried? | Verdict |
|---|---|---|---|
| psp, dc, naomi, atomiswave | GL-era `0xc0000028` STATUS_BAD_STACK — `context_destroy` recursed into the GL driver on the small libco stack (origin commit `5d6a430`, 2026-07-05) | psp Vk-arm removal noted as "valid alternative" in config (64 MiB stack `43cb002` covers it) but never needed | **keep** — PPSSPP self-persists; flycast has nothing to flush |
| ps2 | same GL rationale initially; real reason is renderer-independent: PCSX2 `context_destroy` deadlocks (MTGS::CloseGS) in the single-thread model | **tried & REVERTED 2026-07-15** — watchdog exit-70 killed the worker on *every* ps2 close; the 64 MiB stack fixed the crash but not the deadlock | **keep, never remove** |
| gc, wii | GL shared-context crash class (copied pattern) | no — and no need: `retro_unload_game` flushes anyway | **keep** |

There is one shared flag consulted identically by both teardown arms (`nanoarch.go:1550` GL,
`:1752` Vk). A renderer-scoped split (per-arm hack names) was designed and is **not needed** —
zero cache benefit, and `HwContextOverride` + the per-launch pill mean any config-time renderer
assumption is unsafe anyway.

---

## 3. G1 — THE bug: Dolphin `Reload()` orphans the pipeline UID cache

This is the actual cause of "Mario Kart Wii Green stutters/jumps every session forever" while
F-Zero GX smooths out after a couple of plays.

**Evidence chain (all from `D:\ArcadeStorage\logs\glworker-2.log` + on-disk files):**
- `GFZE01.uidcache` accumulates across sessions: `Read 337 → 526 → 770 → 875 → 1003 → 1081
  pipeline UIDs`. Healthy.
- `RMCEA4.uidcache` (MKWii Green): `Read 0 pipeline UIDs` on *every* boot; file stuck at ~700 B
  on worker-gl-2 even after full sessions.
- RMCEA4 boots load caches for **two host-config hashes seconds apart** (`…-2C6FFF41` then
  `…-2C6FFF40`) — the signature of a mid-boot `ShaderCache::Reload()` (a per-game/INI graphics
  setting is applied after video init and flips a `ShaderHostConfig` bit). GFZE01 boots show one.

**Source defect** (`VideoCommon/ShaderCache.cpp`, custom Dolphin at `D:\Arcade\build\dolphin`):
`Reload()` (line 81) calls `ClosePipelineUIDCache()` (line 84) and **never reopens the file**.
`LoadPipelineUIDCache()` is only called from `Initialize()` (line 65). After any Reload, every
`AppendGXPipelineUID()` for a newly-seen pipeline writes into a closed file handle → silently
dropped. Games that trigger a Reload at boot therefore lose 100% of new UIDs, every session.

**Fix (one-liner class):** in `Reload()`, after `ClearCaches()`/`LoadCaches()`, call
`LoadPipelineUIDCache()` again (before `CompileMissingPipelines()`, line 99). It re-opens the
file, re-reads/validates existing UIDs (idempotent — the in-memory map merge is by UID), and
leaves the handle positioned for appends. No worker (Go) change involved.

**Deploy:** rebuild `dolphin_custom_libretro.dll` → both ConfDirs' `assets/cores/` while idle
(`curl -s localhost:8000/status` first) → recycle via `scripts/recycle-arcade-glworker.ps1`
(never `Stop-Process -Force`). The new build id makes `coreCache.purge` drop the old cache once
— **one expected cold boot per game**, then it accumulates.

**Acceptance:** same worker, MKWii Green: session A play + clean close → session B log shows
`Read N pipeline UIDs` with N > 0 and growing session-over-session; with
`dolphin_wait_for_shaders: enabled` (already default-on) those UIDs precompile during boot, so
in-race stutter is gone by session 2–3. Watch:
`grep -a "pipeline UIDs" D:\ArcadeStorage\logs\glworker*.log`.

---

## 4. Remaining gaps + work items (priority order)

- **G1 — Dolphin Reload fix** (§3). The payoff item; do first.
- **G4a — psp coreCache stamp (config-only):** add to the `psp:` block —
  `coreCache: {dir: "PSP/SYSTEM/CACHE", purge: ["*.glshadercache", "*.vkshadercache"]}`.
  Save-dir-resident, so the existing mechanism works as-is. Without it, a PPSSPP core update
  reads a stale-format cache.
- **G4b — ps2 cache vs core rebuilds:** `gl_programs.bin` lives under the **system** dir
  (`libretro/system/pcsx2/cache`), which `purgeForeignCoreCache` cannot reach (it joins SaveDir
  and has a containment guard). We rebuild LRPS2 from source, so poisoning is a real risk.
  Either (a) extend `coreCache` with a `systemDir: true` variant (small worker change), or
  (b) document a manual `del …\pcsx2\cache\gl_programs.*` step in the LRPS2 build procedure.
  (b) is fine until the next rebuild.
- **G5 — NVIDIA driver-cache verification (backstop for flycast / paraLLEl-RDP / Beetle):**
  confirm the scheduled-task user's `%LOCALAPPDATA%\NVIDIA Corporation\GLCache` (note: a
  `NVIDIA Corporation` dir already sits inside each ConfDir — the worker's CWD may be receiving
  it, which is actually ideal: per-worker, on D:). Verify it exists, grows, and isn't
  size-thrashing; optionally pin `GL_SHADER_DISK_CACHE_PATH`/size env in
  `register-arcade-glworker-task.ps1` for observability. Vulkan equivalent: driver-managed
  `VkPipelineCache` data under the same tree.
- **G3 — ps2 Vulkan arm has no disk cache:** paraLLEl-GS builds pipelines via Granite, which
  supports on-disk (foz) pipeline caching in some embeddings. Investigate whether the
  from-source LRPS2 exposes it; if yes, point it under `system/pcsx2/cache/`; if no, rely on G5
  and measure. ⚠ Do **not** revisit `skip_hw_context_destroy` for this (§2 — tried & reverted).
- **G2 — dual host-config double-compile (cosmetic):** Reload-affected games (RMCEA4) compile
  ubershaders for the transient boot config (`…FF41`) then again for the final one (`…FF40`)
  every boot — wasted boot seconds and duplicate cache files. After G1 lands, optionally find
  which setting flips (compare the two `ShaderHostConfig` bit hashes) and align the base config
  so boot config == final config. Low value; accept if awkward.
- **G6 — cross-worker cold starts (optional):** each worker has its own cache, so a game warm on
  worker-gl is cold on worker-gl-2. Same core build → cache files are valid on either. If it
  ever annoys: an idle-time script that copies **missing files only** (never overwrite — Dolphin
  caches accumulate independently; a smaller file isn't a subset of a bigger one), both workers
  idle-verified via `/status`, dry-run first (global bulk-job rules). Defer.
- **G7 — crash loss (no action):** Dolphin's uidcache and PCSX2's gl_programs flush on *clean*
  unload. The graceful recycle script is already the mandated path; PPSSPP writes mid-session.
  Nothing new needed.

---

## 5. Execution phases

1. **P1 = G1.** Edit `ShaderCache::Reload()`, rebuild dolphin_custom, close×5 gate on a gc AND a
   wii title (both renderer pills), deploy both ConfDirs, verify acceptance (§3). Keep the prior
   DLL as `dolphin_custom_libretro.pre-uidreload.dll` for rollback.
2. **P2 = G4a + G5.** Config-only psp stamp (deploy both ConfDirs + recycle) + driver-cache
   inspection (read-only).
3. **P3 = G4b + G3.** LRPS2 build-doc note; Granite cache investigation.
4. **P4 = G2 / G6** if warranted after living with P1–P3.

Standard deploy discipline throughout: repo `docker/arcade/config.worker-gl.yaml` is
authoritative → `cp` to BOTH ConfDirs → recycle each worker with the script → check
`/status` for live rooms before any stop. Core DLL swaps: workers stopped, both ConfDirs, keep a
`pre-<change>` copy.

## 6. Quick reference — verification commands

```bash
# UID cache health per game (want growing N on repeat boots of the same worker):
grep -a "pipeline UIDs" /d/ArcadeStorage/logs/glworker.log /d/ArcadeStorage/logs/glworker-2.log | tail

# Per-backend shader cache loads (GL = OpenGL-*, Vk = Vulkan-*):
grep -a "cached shaders" /d/ArcadeStorage/logs/glworker*.log | tail

# Build-stamp isolation armed (must appear each gc/wii boot):
grep -a "core cache" /d/ArcadeStorage/logs/glworker*.log | tail

# On-disk state:
ls -la --time-style=long-iso /d/ArcadeStorage/worker-gl*/libretro/legacy_save/User/Cache{,/Shaders}
ls -la /d/ArcadeStorage/worker-gl*/libretro/system/pcsx2/cache
ls -la /d/ArcadeStorage/worker-gl*/libretro/legacy_save/PSP/SYSTEM/CACHE
```
