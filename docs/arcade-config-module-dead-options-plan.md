# Arcade per-game config module — dead options, stale catalog, inert renderer selector

**Status:** Phase 0 shipped (master `ef62f05`). Phases 1–4 open.
**Opened:** 2026-08-02, from a ps2/Stuntman session.
**Trigger:** toggling "No interlacing (sharper)" returned `Too many options.`

---

## 0. The one-line version

The config module renders every option a *core* supports, but a room is a **(core, renderer)** pair —
and nothing in the site has ever modelled the renderer half. Every defect below is a consequence of
that one missing distinction, plus a catalog that is a hand-made snapshot nothing reconciles.

---

## 1. What is actually wrong

### D1 — Save was impossible on the seven biggest cores *(FIXED, Phase 0)*

`ArcadeGameConfig.js` `buildBody()` posts the **whole rendered option set**, not just edits.
`SaveGameConfig` capped `CoreOptions` at a flat **60**.

| core | rendered options | Save |
|---|---:|---|
| dolphin | 95 | rejected |
| flycast | 88 | rejected |
| mupen64plus_next | 77 | rejected |
| ppsspp | 75 | rejected |
| beetle_psx_hw | 74 | rejected |
| pcsx2 | 62 | rejected |
| genesis_plus_gx | 62 | rejected |
| *(12 other cores)* | ≤48 | worked |

Every option rendered; none could be saved. Only "Reset to defaults" worked — that path skips the
check. The bound is now derived from the catalog.

### D2 — The Graphics selector was inert *(FIXED, Phase 0)*

Renderer keys are excluded from every option catalog by design. A *stored* one therefore matched
nothing and was filed into the **advanced** raw rows — rendered as an editable row, re-submitted on
every save, and then winning at launch, because `ArcadeController` merges a profile's options only as
a **base beneath** the saved config:

```csharp
var merged = new Dictionary<string, string>(renderProfile.Options, StringComparer.Ordinal);
foreach (var kv in gameCoreOptions) merged[kv.Key] = kv.Value;   // saved wins
```

Picking OpenGL for Stuntman stored `RenderProfile=opengl` and wrote `pcsx2_renderer=paraLLEl-GS`
straight back over it.

### D3 — Dead options are shown in every dropdown *(OPEN — the headline defect)*

`GetGameConfig` filters by **core**. Both PS2 profiles share `OptionCore = "pcsx2"`, so the same list
renders under both, including options the selected renderer cannot read:

| shown under | inert because |
|---|---|
| `pgs_ssaa`, `pgs_high_res_scanout`, `pgs_disable_mipmaps`, `pgs_deblur`, `pgs_ss_tex` | paraLLEl-GS-only; dead on any GSdx renderer |
| `upscale_multiplier`, `anisotropic_filtering`, `blending_accuracy` | GSdx-only; dead on paraLLEl-GS |

This generalises: any system whose profiles share an `OptionCore` (ps2, ps1-Beetle, n64-mupen) shows
the union of both renderers' options and marks none of them.

### D4 — The catalog is a snapshot nothing reconciles *(OPEN)*

`core-options-catalog.json` is committed, hand-regenerated, and its own header says
*"Regenerate from each core's libretro_core_options.h / DLL on Ziggy."* **There is no generator
script in the repo.** Nothing compares it to the DLLs actually deployed, and the deployed PS2 core is
a *custom* build (`pcsx2_custom_libretro`) while the catalog was taken from stock. libretro silently
ignores an unknown value token, so any drift is invisible at runtime.

> ⚠ An earlier read of this session claimed the deployed core had **dropped** the `OpenGL` and `Auto`
> renderer tokens. **That claim is withdrawn** — libretro option values are arrays of string
> *pointers* and identical strings are pooled by the linker, so a contiguous ASCII run in the binary
> is not the value list. Token validity is *unverified in both directions*; see Phase 1.

### D5 — Nothing validates what a render profile ships *(OPEN)*

`ArcadeQualityPresetsTests` validates every *preset* key and token against the catalog — which is why
presets are clean. But renderer keys are deliberately absent from the catalog, so
`RenderProfile.Options` is validated by **nothing at all**. A typo'd or retired renderer token would
sit there indefinitely and fail silently.

### D6 — The PS2 profile set is wrong on the merits *(OPEN)*

| profile | label | sets | HwContext |
|---|---|---|---|
| `vulkan` | "Vulkan (paraLLEl-GS)" | `pcsx2_renderer=paraLLEl-GS` | `vulkan` |
| `opengl` | "OpenGL" | `pcsx2_renderer=OpenGL` | `gl` |

Two problems:

1. **The label lies.** `Vulkan` and `paraLLEl-GS` are *different GS implementations* that both run on
   a Vulkan surface. The profile called "Vulkan" selects paraLLEl-GS. PCSX2's own Vulkan (GSdx)
   backend — where the GameDB hardware fixes apply — **is not exposed by the site at all.**
2. **The GL profile contradicts the system.** `config.worker-gl.yaml`'s ps2 block is now
   `isGlAllowed: false` + `hwContext: "vulkan"`. The `opengl` profile pins `HwContext="gl"` on a
   system where GL is switched off. It is a leftover from the pre-Vulkan era.

---

## 2. How we got here

Three ordinary decisions that were individually reasonable and collectively produced this.

**(a) PS2 shipped GL-first, then moved to Vulkan — and the profile list didn't follow.**
PS2 went live 2026-07-06 on the Windows GL worker, where `pcsx2_renderer: "OpenGL"` was correct and
load-bearing. The Vulkan/paraLLEl-GS arm landed later and flipped the system to `isGlAllowed: false`.
The `opengl` profile survived the transition unexamined because **nothing could reach it** — D1 made
Save fail and D2 made the dropdown inert, so no room ever ran it. Worker logs confirm: across every
`glworker*.log`, PS2 has only ever been handed `paraLLEl-GS`. A dead path that cannot be exercised
cannot be discovered.

**(b) The catalog grew from hand-curated to extracted, but the bounds around it didn't move.**
The 60-option cap was written when the catalog held a handful of hand-tuned entries per core. Folding
in each core's *complete* option set was the right call — it is what makes real tuning possible — but
it silently pushed seven cores past a constant nobody revisited. The cap was never wrong when
written; it was invalidated by a later, unrelated improvement.

**(c) The renderer was modelled twice, at different layers.**
`RenderProfile.Options` delivers the renderer at launch. `CoreOptionsJson` can also carry it, because
bulk seeding wrote it there. Two writers for one key, with saved-options-win precedence, guarantees
the higher-level control loses. The exclusion of renderer keys from the catalog was meant to prevent
exactly this — but exclusion only removed them from the *plain* list and pushed them into the
*advanced* list, which is still a writer.

**The meta-cause:** every guard in this subsystem validates **within** a layer (presets against the
catalog, options against tokens) and none validates **across** layers — catalog against deployed DLL,
profile against catalog, option against renderer. That is why previous passes at "no dead options"
did not stick: they cleaned data without adding a cross-layer check to hold it clean.

---

## 3. The plan

### Phase 0 — unbreak Save + the selector ✅ *shipped, master `ef62f05`*

- Save bound derived from the catalog, not a constant.
- `ArcadeCoreOptionCatalog.IsRendererSelecting()`; renderer keys hidden from the advanced set and
  dropped from submitted options, making the Graphics selector the single source of truth.
- Tests: any key the selector *flips* between two profiles of one core must be renderer-owned.

### Phase 1 — establish ground truth about the deployed cores

Nothing else is trustworthy until this exists.

1. Write `scripts/extract-core-options.ps1` — the generator the header has always assumed. It runs
   against the DLLs in `D:\ArcadeStorage\worker-gl\assets\cores`, i.e. **what is deployed**, not
   stock, and emits `core-options-catalog.json`.
2. Parse the libretro option **structs** (`retro_core_option_v2_definition` — pointer arrays), not
   ASCII runs. This is the specific mistake that produced the withdrawn D4 claim.
3. Diff old vs new and record every drift. This answers the `OpenGL`/`Auto` question with evidence.
4. Commit the generator so regeneration is reproducible and reviewable.

**Exit:** the catalog provably matches the deployed cores, and a re-run is a no-op.

### Phase 2 — model the renderer half (kills D3)

1. Add renderer applicability to `CoreOption` — which profile(s)/`HwContext` each option is live for.
2. Populate it empirically. The fork already ships the instrument:
   `[opt] DEAD keys (provided but core NEVER queried — value INERT)` and `[opt] reconcile: M/N read`.
   Boot each system once per renderer with the full option set provided and record what the core
   actually reads. Empirical, not guessed — and it also re-proves the pgs/GSdx split above.
   ⚠ "not queried in the sample window" ≠ "not applicable"; anything ambiguous stays visible and gets
   flagged for a longer run rather than being hidden on weak evidence.
3. Filter `GetGameConfig` by the selected profile so only live options render.
4. Split `ArcadeQualityPresets.UltraLiveSpec` from core-keyed to `(core, hwContext)`-keyed — today
   `UltraLiveSpec["pcsx2"]` mixes both renderers' keys, so the modal's *baseline* is a blend and
   switching renderers cannot change the displayed values even with D2 fixed.

**Exit:** switching Graphics visibly changes the option set, and no shown option is inert.

### Phase 3 — fix the PS2 profiles (D6)

Replace the two profiles with the renderers that are real on a Vulkan-only arm:

- **paraLLEl-GS** (`hwContext: vulkan`) — today's default, keeps the `pgs_*` levers.
- **Vulkan (GSdx)** (`hwContext: vulkan`) — PCSX2's native backend, where the GameDB hw-fixes and
  `upscale_multiplier`/`anisotropic`/`blending` apply. Currently unreachable from the site.

Retire the `opengl` profile unless Phase 1 proves the token live *and* GL is re-enabled for ps2 —
it cannot work while `isGlAllowed: false`. Relabel so "Vulkan" stops meaning paraLLEl-GS. Verify each
survivor actually boots and streams (`test-roms`) before it is offered; a renderer that cannot
zero-copy into the capture path must not appear in the list.

**Exit:** every offered PS2 profile has been booted and streamed at least once.

### Phase 4 — close the loop so this cannot recur

1. **Test:** every `RenderProfile.Options` key/token validates against the catalog (closes D5 — the
   guard that would have caught a retired renderer token).
2. **Test:** every option a profile can show is live for that profile (closes D3 structurally).
3. **CI drift check:** re-run the Phase 1 extractor against the deployed cores and fail on
   difference, so catalog/DLL drift is caught by the build, not by a player.
4. **Data cleanup:** drop `pcsx2_renderer` from the 18 PS2 `ArcadeGameProfile` rows (dry-run first),
   then `arcade-gameconfig-export`. Until this runs, `game-overrides.json` still ships the key and
   the selector stays overridden for those titles *even with Phase 0 deployed*.

---

## 4. Standing rule this establishes

> A player-facing option list is a claim that every entry does something. Validate **across** layers —
> deployed DLL → catalog → profile → rendered option — or the claim rots silently, because libretro
> never errors on a key or token it does not recognise.
