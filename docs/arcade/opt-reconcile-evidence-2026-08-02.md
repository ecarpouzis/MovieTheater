# Arcade `[opt] reconcile` / DEAD-keys evidence sweep

Gathered read-only from the live worker logs on Ziggy. No files modified, nothing restarted.

**Sources parsed (retro/GL pool only — capture lane `glworker-3.log` is out of scope per task):**
- `D:\ArcadeStorage\logs\glworker.log` (worker 1, UDP 8446) — covers roughly 2026-07-22 to 2026-08-02, 13.8 MB, ~158 reconcile / 67 DEAD-key events in this window.
- `D:\ArcadeStorage\logs\glworker-2.log` (worker 2, UDP 8447) — covers roughly 2026-07-19 to 2026-08-02, 17 MB, ~182 reconcile / 63 DEAD-key events.
- The rotated `.1` siblings (`glworker.log.1` = 544 MB, `glworker-2.log.1` = 674 MB, older history back into July) were **not** parsed — too large to justify for this sweep given the current logs already cover every system/core of interest with recent, representative samples. Flagged below wherever that matters.

**Method:** streamed each log line-by-line (ANSI codes stripped), tracked the most recent `New room … room="sv-<user>-<game>-<slot>-<systemtag>___<title>"` line, the most recent `System >>> <core>` line, the most recent `[opt] providing N core options: …` line (this is also where renderer/backend option values live), and any `hw render context: …` line, then attached that context to every `[opt] reconcile` / `[opt] DEAD keys` / `[opt] no core options provided` line that followed. Full per-event data: `worker1.csv` / `worker2.csv` in this same scratchpad directory; grouped roll-up: `core-summary.txt`.

**Known data-quality artifact (read this before trusting the `[system]` tag):** the room-id parser only matches when CloudRetro's `New room` line quotes the room value. When a game title contains no spaces (e.g. `Daxter`) or certain punctuation, CloudRetro logs it **unquoted** and a small number of events keep the *previous* room's stale system tag until the next quoted `New room` line resets it. This was caught directly: a `psp`/`Daxter` boot was initially mis-tagged `gc`/`F-Zero GX` (the prior room). After widening the regex to accept the unquoted form, all but a handful of low-count outliers self-corrected. The remaining outliers are physically impossible pairs (e.g. `ParaLLEl N64` tagged `snes`/`nds`/`genesis`/`ps2` with a stale game title from an unrelated earlier room on a reused OS thread id) — **the core identity is authoritative in those cases**, not the tag, and the write-up below re-attributes them to their real system (n64) rather than repeat the artifact. Below, `[system]` is the corrected value; the raw tag is noted only where it fooled the first pass.

Several of the July 30 13:25–13:30 events (snes→ps1→gc→psp in a tight sequence, same `cid`) are clearly a smoke-test/verification pass cycling through systems back-to-back, not organic play — useful because it's exactly the kind of session that gives one clean sample per system, but it means the PSP and GC samples below are **thin** (1–3 events).

---

## PS2 — `LRPS2` (pcsx2 fork), renderer `pgs_renderer`/`pcsx2_renderer=paraLLEl-GS`

Every PS2 room observed in this window used the same renderer config:
`pcsx2_renderer="paraLLEl-GS"`, `pcsx2_upscale_multiplier="2x Native (~720p)"`, hwctx `Vulkan (version 4198400.0)`. (Task asked about `pgs_*` keys specifically — **none appear anywhere in either log**; the site/config sends `pcsx2_*`-prefixed keys only, never `pgs_*`. No evidence `pgs_*` keys are ever provided at all.)

- Reconcile: **6/9 read**, every single sample (5 most recent shown):
  - worker1 2026-08-02 12:24:24.9151 — 6/9
  - worker1 2026-08-02 11:58:06.2802 — 6/9
  - worker1 2026-08-02 01:41:33.8498 — 6/9
  - worker2 2026-08-01 15:32:37.2825 — 6/9
  - worker1 2026-08-01 15:32:26.8562 — 6/9
- **DEAD keys (consistent across all 5 DEAD events, identical set every time):**
  `pcsx2_anisotropic_filtering, pcsx2_blending_accuracy, pcsx2_upscale_multiplier`
  - This directly answers the task's question: under paraLLEl-GS, `pcsx2_renderer` itself IS read (it's in the 6 live keys), but `pcsx2_upscale_multiplier`, `pcsx2_anisotropic_filtering`, and `pcsx2_blending_accuracy` are provided and **never queried** by the core in this configuration.
- Games observed: Stuntman (USA), 007 - Agent Under Fire (USA).
- Evidence: `D:\ArcadeStorage\logs\glworker.log` @ 2026-08-02 12:24:24 (and the other timestamps above, same file + `glworker-2.log`).
- **No evidence** of any other `pcsx2_renderer` value (e.g. software, OpenGL/D3D11/D3D12) in either current log — paraLLEl-GS is the only PS2 renderer this sweep observed.

---

## PS1 — two cores seen, and a cross-core key-bleed finding

### `Beetle PSX HW`, renderer `beetle_psx_hw_renderer=hardware_vk`
- Only **1** sample in the current window: worker2 2026-07-30 13:26:39.2343 — **9/9 read, 0 DEAD keys.** hwctx `Vulkan (version 4194336.0)`. Game: CTR - Crash Team Racing (USA).
- Thin sample (smoke-test session), but clean: every option this core was given, it queried.

### `PCSX-ReARMed` (software dynarec), same room-config key set
- 20 events, worker2 only. Reconcile stat is **consistently 6/9 read** across all 5 most-recent samples (2026-08-01 12:45:57, 2026-07-31 01:46:15, 01:45:57, 01:38:11, 01:14:06).
- **DEAD keys (identical set on every DEAD event):**
  `beetle_psx_hw_renderer, pcsx_rearmed_pad1type, pcsx_rearmed_pad2type`
- **This is the interesting finding:** the site is handing the PS1 room's option set `beetle_psx_hw_renderer="hardware_vk"` even when the game actually launches under `PCSX-ReARMed` (a pure-software core with no HW renderer concept at all) — so that key is inert there by construction, exactly the cross-core key-bleed the task asked to look for. Additionally `pcsx_rearmed_pad1type`/`pad2type` are provided (`="dualshock"`) but this core build never queries them either — a PCSX-ReARMed-native option, not a mismatched one, still DEAD.
- Games: Castlevania - Symphony of the Night (USA), Castlevania Rondo of the Night v1.7 [Hack], CTR - Crash Team Racing (USA).
- Evidence: `D:\ArcadeStorage\logs\glworker-2.log` @ 2026-08-01 12:45:57 (and the other 4 timestamps above).

---

## N64 — two cores, several renderer combinations; the richest dataset (172 + 47 + 42 events)

Re-attributed to `n64` from core identity per the caveat above (raw tags on some events were stale: `genesis`/`snes`/`nds`/`ps2` — impossible given these are N64-only cores; sample game titles for those specific stale-tag events, e.g. "Learn 2 Kaizo v1.1 (Super Mario World) [Hack]" attached to a ParaLLEl N64 boot, confirm the tag (not the core) was wrong).

### `Mupen64Plus-Next`, rdp-plugin=`parallel` / rsp-plugin=`parallel` (most common combo, hwctx Vulkan 4198400.0)
- 62+11 reconcile events (two provider-key-order variants of the same config). Consistently **15/16 read**.
- **DEAD key (single key, every single time):** `mupen64plus-169screensize`
  - i.e. under the parallel RDP plugin, only the 16:9 screensize hint goes unread — everything else (43screensize, EnableCopy*ToRDRAM, EnableLegacyBlending, multisampling, bilinearmode, parallel-rdp-upscaling, rdp/rsp-plugin) is live.
- Most recent: worker2 2026-08-01 23:29:23.5371 (15/16); worker1 2026-08-01 23:09:34.0099; 22:34:34.1419; 21:52:46.9790; 12:39:01.5266.
- Games: The Missing Link, Mario Party 64, Peach's Fury, SM64 Last Impact, Mario Kart 64 (USA), Super Mario 64 (USA) - BAZR [Hack].
- Evidence: `D:\ArcadeStorage\logs\glworker.log` + `glworker-2.log`, 2026-07-30 through 2026-08-01.

### `Mupen64Plus-Next`, rdp-plugin=`gliden64` / rsp-plugin=`hle`
- 9 reconcile events, **17-19/19-21 read** depending on room.
- **DEAD keys, small set:** `mupen64plus-169screensize, mupen64plus-parallel-rdp-upscaling`
  - Only these two are dead under gliden64 (both are parallel-RDP-specific knobs, which makes sense — gliden64 doesn't consume them).
- A second, larger DEAD set was seen in an earlier/rarer variant of this same renderer combo (worker1 2026-07-29 21:41:49, 2026-07-23 10:27:00, both **17-18/26-27 read**):
  `mupen64plus-169screensize, mupen64plus-bilinearmode, mupen64plus-enablecopyauxtordram, mupen64plus-enablecopycolortordram, mupen64plus-enablecopydepthtordram, mupen64plus-enablelegacyblending, mupen64plus-enablenativerestexrects, mupen64plus-multisampling, mupen64plus-parallel-rdp-upscaling`
  — i.e. when the full config.yaml option superset is provided (not just the per-room override), gliden64 leaves **9** framebuffer/AA/RDP keys dead: all the `EnableCopy*ToRDRAM`/`EnableLegacyBlending`/`multisampling`/`bilinearmode` options (the config.yaml comment block for these — "fixes DKR/Bomberman FB effects" — only applies to the parallel RDP plugin, confirmed here).
- Evidence: `D:\ArcadeStorage\logs\glworker.log` @ 2026-07-29 21:41:49 / 2026-07-23 10:27:00; `glworker-2.log` @ 2026-07-31 20:56:14 / 20:51:38 / 2026-07-30 15:25:47.

### `ParaLLEl N64` (standalone core, build `3981986`), gfxplugin=`glide64` / rspplugin=`hle`
- 28 reconcile events, mostly **8/8 read** (clean), occasionally 10/12 or 16/16 depending on how many options that room's config carried.
- **DEAD keys when present:** `mupen64plus-AllowUnalignedDMA, mupen64plus-CountPerOp`
  - Note the `mupen64plus-` prefix on keys sent to the `ParaLLEl N64` core (which natively uses `parallel-n64-*`) — these look like a config-module cross-contamination of the two N64 core's option namespaces, same shape as the PS1 beetle/pcsx_rearmed finding above.
- Most recent: worker1 2026-08-01 09:49:32.0560 (8/8); worker2 2026-07-31 20:53:47.5436; worker1 2026-07-30 15:47:19.8593 (10/12, DEAD present).
- Games: SM64 Last Impact, OoT 4-Player hacks, Super Mario 64 Split-screen, Zelda OoT Master Quest debug.
- Evidence: `D:\ArcadeStorage\logs\glworker.log` + `glworker-2.log`, 2026-07-29 through 2026-08-01.

### `ParaLLEl N64`, gfxplugin=`parallel` / rspplugin=`parallel` (hwctx Vulkan 4194316.0)
- 9-12 reconcile events, **7/7 read** most commonly.
- **DEAD keys when the larger config lands:** `mupen64plus-AllowUnalignedDMA, mupen64plus-CountPerOp, mupen64plus-pak1, mupen64plus-pak2, mupen64plus-pak3, mupen64plus-pak4` (same cross-namespace `mupen64plus-*` keys, plus the 4 controller-pak options going unread).
- Most recent: worker1 2026-08-01 12:00:06.5955 (7/7); 11:59:48.7079; 11:30:45.9401; 11:17:45.3900; 09:49:49.3291.
- Evidence: `D:\ArcadeStorage\logs\glworker.log`, 2026-08-01.

### `ParaLLEl N64` (build `4fc9396`), gfxplugin=`gliden64` / rspplugin=`hle`
- 4 reconcile events, **5/11 read** — the worst ratio observed for any N64 combo.
- **DEAD keys:** `parallel-n64-gliden64-enablecopyauxtordram, parallel-n64-gliden64-enablecopycolortordram, parallel-n64-gliden64-enablecopydepthtordram, parallel-n64-gliden64-enablefbemulation, parallel-n64-gliden64-enablelegacyblending, parallel-n64-gliden64-enablenativerestexrects`
  — this time the keys ARE correctly `parallel-n64-gliden64-*` prefixed (not cross-contaminated), so this looks like a genuine "gliden64-under-ParaLLEl-N64 ignores its own FB-emulation knobs" finding, distinct from the Mupen64Plus-Next+gliden64 case above.
- Evidence: `D:\ArcadeStorage\logs\glworker.log` @ 2026-07-23 03:16:49.0760 / 03:15:53.7527.
- **Never seen:** `angrylion` as a value for any N64 renderer key — consistent with the known hard-panic rule; expected absence, not a gap.

---

## GameCube / Wii — `dolphin-emu`, renderer `dolphin_renderer=Hardware`, hwctx Vulkan 4194304.0

Both systems confirmed to share the identical option set and both boot cleanly:

- **[gc]** 3 reconcile events (F-Zero GX (USA)): worker2 2026-07-31 21:36:42.9728 — **10/10**; 2026-07-30 13:34:24.8895 — 8/8; 2026-07-30 13:27:58.2540 — 8/8. **0 DEAD keys in any GC sample.**
- **[wii]** 10 reconcile events (Project REX, Super Smash Bros Infinite/Universe, Mario Kart Wii Deluxe X Green Hack): worker2 2026-07-31 22:45:34.1753 — **11/11**; 22:44:25.2991; 2026-07-31 00:13:34.9102; worker1 00:12:35.0510; 00:11:25.3953 — all clean. **0 DEAD keys.**
- Providing string (both): `dolphin_anti_aliasing="2", dolphin_cpu_core="JIT64", dolphin_efb_scale="3", dolphin_ir_mode="1", dolphin_ir_modifier="None", dolphin_main_cpu_thread="disabled", dolphin_max_anisotropy="4", dolphin_renderer="Hardware", dolphin_shader_compilation_mode="2", dolphin_swing_modifier="L2", dolphin_wait_for_shaders="enabled"`.
- Evidence: `D:\ArcadeStorage\logs\glworker.log` @ 2026-07-22 02:10:36 / 23:10:20 (early GC/Wii samples); `glworker-2.log` @ 2026-07-31 21:36–22:45 (most recent).
- **Nothing is DEAD for dolphin-emu anywhere in this window** — every option provided is queried, on both GC and Wii.

---

## PSP — `PPSSPP`, hwctx Vulkan 4194322.0 — very thin sample, from the smoke-test run

- **Only 1 event total in both current logs**: worker2 2026-07-30 13:29:27.9874 — **5/5 read, 0 DEAD**.
- Providing: `ppsspp_cpu_core="JIT", ppsspp_fast_memory="enabled", ppsspp_internal_resolution="2880x1632", ppsspp_smart_2d_texture_filtering="enabled", ppsspp_texture_anisotropic_filtering="16x"`.
- Game: Daxter (this room's `New room` line was logged unquoted — `room=sv-33-55384-0-psp___Daxter` — which is what first threw off the tagger; corrected).
- Evidence: `D:\ArcadeStorage\logs\glworker-2.log` @ 2026-07-30 13:29:22 (New room) / 13:29:27 (reconcile).
- **Caveat:** one sample is not enough to claim "no DEAD keys under PPSSPP" as a general fact — per the ppsspp-core skill, the custom build's option surface is narrow by design (aniso/fastmem/cpu_core only), so a clean 5/5 is plausible, but this needs a second/third room to be trusted the way the N64/PS2/PS1 findings can be.

---

## Dreamcast — `Flycast`, renderer keys `reicast_*` (confirmed correct prefix), hwctx Vulkan 4194304.0

- 3 reconcile events, all **3/3 read, 0 DEAD**: worker1 2026-08-02 01:48:10.6000; worker2 01:46:02.1738; worker1 01:43:57.6932.
- Providing: `reicast_alpha_sorting="per-triangle (normal)", reicast_anisotropic_filtering="16", reicast_internal_resolution="2560x1920"`.
- Game: Sonic Adventure v1.005 (1999)(Sega)(US).
- Evidence: `D:\ArcadeStorage\logs\glworker.log` + `glworker-2.log` @ 2026-08-02 01:43–01:48.
- Confirms the config-module skill note that flycast's option prefix is `reicast_`, not `flycast_` — the site is sending the right prefix and getting full reconciliation.

---

## Saturn — `Kronos`, key `kronos_sh2coretype`

- 6 events total (3 reconcile + 3 matching DEAD), stat **5/6 read** every time.
- **DEAD key, single, every time:** `kronos_sh2coretype` (value `"kronos"`) — the SH2 interpreter/dynarec-selection key is provided but never queried by this Kronos build.
- Providing: `kronos_addon_cartridge="none", kronos_bandingmode="enabled", kronos_force_hle_bios="disabled", kronos_meshmode="enabled", kronos_sh2coretype="kronos", kronos_videoformattype="auto"`.
- Games: "christmas nights into dreams touki genteiban", "guardian heroes (jpn)".
- Evidence: `D:\ArcadeStorage\logs\glworker.log` @ 2026-07-22 12:24:34.8251; `glworker-2.log` @ 2026-07-22 12:49:44.0526 / 12:20:45.2354.

---

## 3DS — `Citra`

- 49 events (two build variants, `e3e057f` and `e3e057f-dirty`). Reconcile is always a **perfect ratio** (5/5, 3/3, 2/2, 1/1 depending on room) — **0 DEAD keys ever observed** for Citra in either log.
- Two provider states seen: a bare (no-renderer-key) minimal option set, and a Vulkan hwctx variant (4198400.0). Both fully reconcile.
- Sample games: Mario Kart 7, 50 Classic Games 3D, Yoshi's New Island, Monster Hunter XX Double Cross (+ English patch), Luigi's Mansion Dark Moon.
- Evidence: `D:\ArcadeStorage\logs\glworker.log` + `glworker-2.log`, spread 2026-07-24 through 2026-07-26.
- 2 additional Citra boots logged **"no core options provided (core runs all-defaults)"** — some rooms get zero per-game overrides at all.

---

## DS — `melonDS DS`

- 45 events on the primary build (`1.2.0`), 4 on a `RelWithDebInfo` build. Reconcile mostly **9/9 or 10/10 read**.
- **DEAD keys (2 distinct sets observed, both from July 23-24, none in the most recent 5 samples):**
  - `melonds_boot_mode, melonds_console_mode, melonds_number_of_screen_layouts, melonds_render_mode, melonds_screen_layout1, melonds_sysfile_mode, melonds_touch_mode` (worker1 2026-07-23 20:35:22.8674)
  - same set + `melonds_opengl_resolution, melonds_show_cursor` (worker2 2026-07-24 18:14:45.5425)
- The 5 most-recent reconcile samples (worker2 2026-08-01 12:46:31 through worker1 2026-07-28 18:01:53) are all clean 9/9 with no accompanying DEAD line — so whatever caused those two July 23-24 DEAD events (likely a room that got the full config.yaml default option superset rather than the narrower per-room set) hasn't recurred recently. Sample size and recency both matter here — treat the DEAD set as "possible under some option-set combination," not "current steady state."
- Games: New Super Mario Bros., Phoenix Wright Ace Attorney - Justice For All, Mario Kart DS, Pokemon Platinum.
- Evidence: `D:\ArcadeStorage\logs\glworker.log` + `glworker-2.log`, 2026-07-23 through 2026-08-01.

---

## 2D cores that never get per-room options (no DEAD-key evidence possible)

These systems' rooms consistently log **`[opt] no core options provided (core runs all-defaults)`** — the config module isn't sending them any per-game key/value pairs at all in this window, so there is nothing to reconcile and no DEAD keys can appear:

- **Genesis Plus GX** (`genesis`, and also `segacd` — same core handles both): 54 + 1 boots, all "no options provided". Games span Sonic titles, ROM hacks, and one Sega CD prototype.
- **Snes9x** (`snes`): 9 boots, all "no options provided".
- Other Citra boots noted above (2 of 49) also hit this path.

Cores that DID get options and reconciled perfectly (no DEAD keys ever seen):
- **Nestopia** (`nes`): 3 events, providing string not renderer-flavored (2D, no HW key), all clean.
- **mGBA** (`gb`/`gbc`/`gba`, single core spans all three): 11 events across the three tags, all reconcile cleanly where options are provided at all (some `gbc` boots also hit "no options provided").
- **Gearcoleco** (`coleco`): 1 boot, "no options provided".
- **ScummVM** (its own `[scummvm]` system tag, used for point-and-click regardless of the box-art platform tag it's filed under): 35 events, **always exactly 1/1 read** — it only ever exposes a single option (`scummvm_video_hw_acceleration="disabled"`), so a DEAD key is structurally impossible here; 0 ever seen.

---

## Combinations with NO evidence in the sweeped logs

- **Naomi and Atomiswave**: zero room-id tags of either kind (`naomi`, `atomiswave`) appear anywhere in either current log (checked against all 500+ distinct `New room` system-tag matches). Only `dc` (Dreamcast) room tags were seen for the Flycast core. Either these systems haven't been played in this ~2-week window, or their content is filed under the `dc` tag rather than distinct ones — can't distinguish from the logs alone; this needs a look at `ArcadeGame.System` values in the DB to settle, which is out of scope for a log-only sweep.
- **PS2 renderer values other than `paraLLEl-GS`**: no `pcsx2_renderer` value besides `paraLLEl-GS` was observed; no evidence of software/OpenGL/D3D11/D3D12 PS2 rendering being exercised recently.
- **`pgs_*`-prefixed PS2 option keys**: never appear at all (provided or DEAD) in either log — the task's premise that these might show up as DEAD vs `pcsx2_*` doesn't apply; the site simply never sends `pgs_`-prefixed keys.
- **Beetle PSX HW with a non-Vulkan renderer** (e.g. `hardware_gl` or software): only `hardware_vk` was observed, and only once.
- **`angrylion`** as an N64 RDP value: never seen, consistent with the documented hard-panic and the deliberate avoidance of it.
- **The rotated `.1` logs** (`glworker.log.1`, `glworker-2.log.1`) were not parsed — they hold roughly 500-670 MB each of additional July history. If a system above needs a deeper/longer sample (PSP and GC especially, given 1 and 3 events respectively), that's where to look next.
- **The capture-lane worker** (`glworker-3.log`, zone=capture, heavy titles like yuzu/Citra-capture/etc.) was explicitly out of scope per the task and not examined; it doesn't run the same libretro-core `[opt] reconcile` instrumentation path in the same way as the retro/GL pool (it captures a whole running window rather than driving a libretro core through core-option env callbacks in the same sense — worth confirming separately if capture-lane option reconciliation is ever needed).

---

## Caveats

1. **Sample-window ambiguity.** A key not queried in a short/quiet room is not proof it's permanently inert — a game that never hits the code path reading that option (e.g. a boot screen vs. in-game) could look DEAD in one room and live in another. The multi-sample combos above (PS2, PS1/PCSX-ReARMed, both N64 cores, melonDS) show the SAME DEAD set recurring across many independent rooms/games/timestamps, which is much stronger evidence than the single-sample combos (PSP, Beetle PSX HW, GC, Saturn's small n).
2. **System-tag correlation is a log-parsing heuristic**, not something CloudRetro records structurately alongside every reconcile line. It was cross-checked against known-impossible core/system pairs (an N64 core can't run an SNES ROM) and corrected/annotated everywhere it produced a contradiction; treat the `[system]` label as derived-and-verified, but the underlying room-id tag string in the raw CSVs occasionally lags by one room when a title has no spaces.
3. **"DEAD" per the worker's own wording means "provided but never queried by this core build in this run"** — it does not by itself prove the KEY NAME is wrong (misnamed) vs. the VALUE being structurally unusable for that render path (e.g. RDP-plugin-specific keys under a different RDP plugin) vs. a genuinely unused/vestigial option. The per-combo sections above try to note which of those is more likely from the key names themselves, but that's inference, not something the log states directly.
4. **Two workers, same coordinator, same config** — worker1 (8446) and worker2 (8447) run identical `docker/arcade/config.worker-gl.yaml`-derived config, so events are pooled across both as one dataset; no config drift between them was observed in any of the "providing" strings compared.
5. Raw structured data backing every claim above: `worker1.csv`, `worker2.csv` (per-event), `core-summary.txt` (grouped roll-up) — all in this scratchpad directory, generated by `parse-opt-log.ps1` + `summarize.ps1` also saved there.

---

# Appendix — Phase 3 boot tests (2026-08-02, PS2 render profiles)

Added by the Phase 3 pass of `docs/arcade-config-module-dead-options-plan.md` (D6). The sweep above
could only report what had *already* been booted, and PS2 had only ever run one renderer
(paraLLEl-GS), so D6 could not be settled from logs alone. These are **live boots through the
deployed prod site** (`theater.carpouzis.com`, harness `.claude/skills/test-roms/arcade-diag.mjs`),
driven only through already-deployed behaviour — the Phase 2 code is NOT on prod, so nothing here
depends on it.

**Method — how a renderer the site cannot offer was reached anyway.** The launch path merges a
game's saved `ArcadeGameProfile.CoreOptionsJson` *over* the render profile's own options
(`ResolveGameConfigAsync` → `merged[k] = v`, saved wins — defect D2), so a temporary DB row carrying
`{"pcsx2_renderer":"Vulkan"}` delivers PCSX2's real Vulkan (GSdx) backend to a room even though no
render profile offers it. Row inserted for the test (`ArcadeGameProfile` Id 30, System `ps2`,
TitleKey `shin megami tensei - persona 3 fes`, no `RenderProfile`/`HwContext` set,
`pcsx2_enable_hw_hacks` deliberately untouched so the GameDB auto-fixes stay live) and **deleted
immediately after A1** (verified: Id 30 gone, ps2 rows back to 17). None of the 17 seeded rows was
read into, updated or deleted.

**Why this title.** `Shin Megami Tensei - Persona 3 FES` is an enabled `ps2` `ArcadeGame` (Id 60303)
with **no** `ArcadeGameProfile` row of its own, already staged in the JIT ROM cache
(`D:\ArcadeStorage\roms\ps2\...FES (USA).cso`, 4.59 GB, 2026-07-18) and last played 2026-07-18, so it
was known to boot. Using ONE game across all three arms is what makes the comparison exact: the
worker provided the **identical 9-key option set** in every arm, so the only variable is
`pcsx2_renderer`.

Every room was opened and closed by the harness; `localhost:8000/status` showed all three workers
`free` before the first boot and after the last. Nothing was restarted.

## The three arms — same game, same 9 provided keys, one worker (`glworker.log`, UDP 8446)

| arm | started | `pcsx2_renderer` | hw surface | reconcile | DEAD keys |
|---|---|---|---|---:|---|
| **A3** control (system default) | 13:35:17 | `paraLLEl-GS` | Vulkan, zero-copy ACTIVE | **6/9** | `pcsx2_anisotropic_filtering`, `pcsx2_blending_accuracy`, `pcsx2_upscale_multiplier` |
| **A1** Vulkan (GSdx) | 13:29:09 | `Vulkan` | Vulkan, zero-copy ACTIVE | **7/9** | `pcsx2_pgs_high_res_scanout`, `pcsx2_pgs_ssaa` |
| **A2** OpenGL (GSdx) | 13:32:24 | `OpenGL` | **GL** (per-room `hwctx=gl` escape) | **7/9** | `pcsx2_pgs_high_res_scanout`, `pcsx2_pgs_ssaa` |

Provided set, identical in all three (from the `[opt] providing 9 core options:` line):
`pcsx2_anisotropic_filtering="8x"`, `pcsx2_bios="scph39001.bin"`, `pcsx2_blending_accuracy="Medium"`,
`pcsx2_fastboot="enabled"`, `pcsx2_pgs_disable_mipmaps="enabled"`, `pcsx2_pgs_high_res_scanout`,
`pcsx2_pgs_ssaa`, `pcsx2_renderer`, `pcsx2_upscale_multiplier="2x Native (~720p)"`.

**The DEAD sets are an exact mirror.** Under paraLLEl-GS the three GSdx levers are inert; under
either GSdx backend the two paraLLEl-GS levers are inert. This is the first direct proof of the
GSdx half of D3 — the sweep above could only prove the paraLLEl-GS half, because no GSdx room had
ever been booted.

### ⚠ The one key that survives BOTH: `pcsx2_pgs_disable_mipmaps`

It is provided in all three arms and appears in **no** DEAD set — the core reads it under
paraLLEl-GS *and* under both GSdx backends. So the `pcsx2_pgs_` prefix is **not** a reliable
namespace boundary: the name says paraLLEl-GS but LRPS2 routes this one to a shared GS setting.
Any applicability rule that hides the whole `pcsx2_pgs_` prefix on GSdx profiles would hide a
working lever. Recorded here because it is exactly the kind of structural-reasoning failure the
"evidence, not presumption" rule exists to catch — the Phase 2 prefix rule was written on the
structural argument, and this boot test falsified part of it.

## A1 — Vulkan (GSdx) · **PASS**

```
13:29:09.6155  New room ... game="Shin Megami Tensei - Persona 3 FES (USA)"
13:29:09.7798  Vulkan zero-copy armed for "...FES (USA)" (sync=semaphore)
13:29:09.8226  Libretro System >>> LRPS2 (v2.0.0-fe939ae) ...
13:29:09.8690  Libretro [room-cheat] option pcsx2_renderer=Vulkan
13:29:09.8717  Libretro hw render context: Vulkan (version 4198400.0)
13:29:09.8778  Libretro [opt] providing 9 core options: ... pcsx2_renderer="Vulkan" ...
13:29:10.6199  Libretro [vk] device created by core (v1 create_device), queue family 0 (zero-copy extensions injected)
13:29:10.6750  Libretro [vk] zero-copy: pool built (8 slots @ 1280x896)
13:29:10.6750  Libretro [vk] zero-copy: ACTIVE (sync=semaphore(layer1))
13:29:10.6750  Libretro vulkan capture: device ready, zero-copy ACTIVE (frames imported to GL, no readback)
13:29:14.8781  Libretro [opt] DEAD keys (...): pcsx2_pgs_high_res_scanout, pcsx2_pgs_ssaa
13:29:14.8786  Libretro [opt] reconcile: 7/9 provided keys were read by the core
```

Stream health (75 s, `--idle`): 1280x896 AV1, **fps 59–60 throughout**, `framesDecoded` 4146,
`freezeCount` 0, `totalFreezesDuration` 0, `framesDropped` 0, video jitter buffer 14–16 ms,
decode 3.4 ms, nack 0, pli 0. **Content verified** (`diag-p3fes-vk-gsdx.png`): a real in-engine
battle scene — four party portraits with HP/SP bars, the "RUSH" prompt, a lit 3D attack effect over
textured geometry. Not a flat field.

**`pcsx2_upscale_multiplier` is READ here** (absent from the DEAD set) — the direct answer to the
task's question, and the whole reason a GSdx profile is worth offering: the upscale / anisotropic /
blending levers only exist on this side.

**Zero-copy works.** This was the verify-before-offer bar the plan set, and Vulkan (GSdx) clears it
identically to paraLLEl-GS (`zero-copy: ACTIVE (sync=semaphore(layer1))` in both arms).

⚠ **What A1 did NOT prove: the GameDB claim.** D6 justifies a GSdx profile partly on "where the
GameDB hardware fixes apply". This test cannot speak to that — Persona 3 FES matched no GameDB
hardware fix at all (`[GameDB] Searching for patch with CRC '94A82AAA'` → `No CRC-specific patch or
default patch found`, `Serial: SLUS-21621`, no `Enabled GS Hardware Fix` lines), while the
paraLLEl-GS Stuntman room earlier the same day (12:24:20) *did* log
`Enabled GS Hardware Fix: cpuSpriteRenderBW/halfPixelOffset`. So GameDB fixes are logged on the
paraLLEl-GS path too, and whether they take EFFECT differently per backend is untested. The GSdx
profile is offered on the reconcile evidence (levers that provably work), not on the GameDB claim.

## A2 — OpenGL (GSdx), the per-room GL escape · **PASS**

The open question was whether the designed W3-F1 per-room `hwctx=gl` override still works for ps2
now that `config.worker-gl.yaml`'s ps2 block is `isGlAllowed: false` + `hwContext: "vulkan"`. It
does, and the worker says so explicitly:

```
13:32:24.7889  New room ... game="Shin Megami Tensei - Persona 3 FES (USA)"
13:32:24.9040  Per-game hw context: "...FES (USA)" -> "gl" (core default "vulkan", via per-request override)
13:32:24.9218  Libretro hw context override for this room: "gl" (core default "vulkan")
13:32:24.9255  Libretro System >>> LRPS2 (v2.0.0-fe939ae) ...
13:32:24.9367  Libretro [room-cheat] option pcsx2_renderer=OpenGL
13:32:24.9398  Libretro [opt] providing 9 core options: ... pcsx2_renderer="OpenGL" ...
13:32:24.9663  Libretro Created an OpenGL context
13:32:24.9669+ Libretro [proc] glGetError / glBindBufferRange / glMapBufferRange / ... (real GL entry points resolved)
13:32:29.9398  Libretro [opt] DEAD keys (...): pcsx2_pgs_high_res_scanout, pcsx2_pgs_ssaa
13:32:29.9398  Libretro [opt] reconcile: 7/9 provided keys were read by the core
```

**No `rejected non-GL hw render context type`** anywhere in the room. `isGlAllowed: false` gates the
*inferred/default* path, not the explicit per-room override — the yaml's own psp/dc/gc comments said
so, and this is the confirmation for ps2.

Stream health (70 s, `--idle`): 1280x896 AV1, **fps 60 flat**, `framesDecoded` 2994, `freezeCount` 0,
`framesDropped` 0, jitter buffer 6–9 ms, decode 3.0 ms, nack 0, pli 0. Worker pacing was clean the
whole run: `pace-diag ticks/s=59.9` with `slowTicks(>20ms)=0` and `meanTick` 2.5–5.7 ms on every
sample after warm-up. **Content verified** (`diag-p3fes-gl.png`): the Iwatodai strip mall in
daylight — textured geometry, NPCs, shop signage legible ("Bookworms Used Books"), the
"After School" HUD banner. Real rendering, not a clear-colour field.

**Zero-copy: not applicable, and that is not a failure.** The GL arm logs no `zero-copy` line at all,
because zero-copy on this worker is the *Vulkan→GL import* path (`frames imported to GL, no
readback`) — a core already rendering into the worker's GL context has nothing to import. The plan's
"must zero-copy into the capture path" bar is a Vulkan-side test; the GL equivalent is that the core
renders directly into the capture surface, which `Created an OpenGL context` + a clean 60 fps stream
demonstrates.

## A3 — paraLLEl-GS control · baseline refreshed

```
13:35:17.3039  New room ... game="Shin Megami Tensei - Persona 3 FES (USA)"
13:35:17.4456  Libretro [room-cheat] option pcsx2_renderer=paraLLEl-GS
13:35:17.4477  Libretro [opt] providing 9 core options: ... pcsx2_renderer="paraLLEl-GS" ...
13:35:17.6652  Libretro [vk] zero-copy: ACTIVE (sync=semaphore(layer1))
13:35:22.4479  Libretro [opt] DEAD keys (...): pcsx2_anisotropic_filtering, pcsx2_blending_accuracy, pcsx2_upscale_multiplier
13:35:22.4479  Libretro [opt] reconcile: 6/9 provided keys were read by the core
```

Same 6/9 and the same three DEAD keys the sweep reported for every earlier paraLLEl-GS room
(Stuntman, 007 Agent Under Fire), now reproduced on a **third, different game** — which strengthens
the original finding from "same config, several rooms" to "same config, several rooms, three games".

## Verdicts fed into Phase 3

1. **Vulkan (GSdx) is real, boots, streams and zero-copies** → offer it as its own profile.
2. **The default profile is paraLLEl-GS, not "Vulkan"** → the label must stop claiming otherwise;
   both are Vulkan-surface, and only one is PCSX2's own GS.
3. **The `opengl` profile works** → keep it (relabelled "OpenGL (GSdx)") rather than retiring it.
   It had never been exercised; now it has.
4. **GSdx levers** (`pcsx2_upscale_multiplier`, `pcsx2_anisotropic_filtering`,
   `pcsx2_blending_accuracy`) are live on the two GSdx profiles and dead on paraLLEl-GS.
5. **paraLLEl-GS levers** `pcsx2_pgs_ssaa` and `pcsx2_pgs_high_res_scanout` are live only on
   paraLLEl-GS — now log-proven rather than structural.
6. **`pcsx2_pgs_disable_mipmaps` must stay visible everywhere** — read by all three.
7. **No evidence either way** for `pcsx2_pgs_deblur` / `pcsx2_pgs_ss_tex` (never provided in any
   room, so no reconcile line can speak to them) — see the Phase 3 note in
   `ArcadeCoreOptionApplicability` for how that ambiguity is handled.

## Harness note

`arcade-diag.mjs` gained a `--profile <label substring>` flag in this pass. Its existing `--hwctx gl`
clicks a dropdown item whose text is literally `Force GL`, which the 2026-07-21 GameModal replaced
with one item per **render profile** (ps2 "OpenGL", n64 "parallel_n64 core · Glide64 (romhack
compat)", ...) — the bare Force-GL/Vulkan pair is now only the profiles-not-yet-loaded fallback, so
`--hwctx gl` could not reach A2 at all. `--profile OpenGL` picks by label and prints the whole menu
it saw.
