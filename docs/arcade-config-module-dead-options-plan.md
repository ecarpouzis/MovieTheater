# Arcade per-game config module — dead options, stale catalog, inert renderer selector

**Status:** Phase 0 shipped (master `ef62f05`). Phases 1–4 open.
**Opened:** 2026-08-02, from a ps2/Stuntman session.
**Trigger:** toggling "No interlacing (sharper)" returned `Too many options.`
**Revised 2026-08-02 (second pass):** fleet-audited. Adds D7 (coverage gaps: ~18 systems have no
config module at all and 17 deployed cores are absent from the extraction), re-keys applicability
by **profile** (not surface — parallel_n64 has two GL profiles reading different plugin keys),
reframes the D6 `opengl` claim (the per-room `hwctx=gl` override is the designed W3-F1 escape, so
"cannot work" was overstated — it is *unverified*, which is different), and switches the Phase 1
extractor to a runtime harness. Full fleet matrix in §5.

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
2. **The GL profile is a pre-Vulkan leftover that has NEVER been exercised.** `config.worker-gl.yaml`'s
   ps2 block is now `isGlAllowed: false` + `hwContext: "vulkan"`, and every glworker log shows only
   paraLLEl-GS. ⚠ *Correction (second pass):* `isGlAllowed: false` does **not** make the profile
   impossible — the per-room `hwctx=gl` override is the **designed** W3-F1 GL escape (the yaml's own
   comments on psp/dc/gc say exactly this: "a per-GAME GL escape must use the explicit
   hwContext:'gl' override field"). What is true: the profile is *unverified in both directions*
   (token liveness in the custom DLL, and whether the GL path still boots + streams + zero-copies).
   Retire or keep it on **boot evidence**, not on the isGlAllowed flag.

### D7 — Coverage gaps: whole systems and cores the module doesn't know exist *(OPEN — new, second pass)*

The module's reach is a fraction of the fleet, and the gap is invisible (the ⚙ button just never
appears):

1. **17 deployed cores are absent from the extraction JSON.** `core-options-catalog.json` holds 15
   cores; the deployed dir (`D:\ArcadeStorage\worker-gl\assets\cores`) holds ~30 distinct cores.
   Missing entirely: `opera` (3do), `same_cdi` (cdi), `stella` (a2600), `prosystem` (a7800),
   `mednafen_pce/ngp/wswan/lynx/vb`, `vecx`, `freeintv`, `gearcoleco`, `freechaf`, `o2em`,
   `amiarcadia`, `potator`, `pokemini`. `kronos` is extracted but from an unverified vintage.
2. **`SystemDefaultCore` doesn't map ~18 systems**, so `HasAnything()` is false and the Configure
   button is suppressed for: sg1000 (!— its core `genesis_plus_gx` IS catalogued), pce, ngpc, wsc,
   a2600, a7800, lynx, vb, vectrex, intv, coleco, channelf, o2em, arcadia, supervision, pokemini,
   3do, cdi.
3. **A stale comment lies about parallel_n64.** The catalog's parallel_n64 block says "the startup
   extraction folds in the full option set on top of these hand-tuned few" — there is no
   `parallel_n64` entry in the JSON, so nothing folds. (That hand-only state is probably RIGHT —
   see the screensize bridge caveat: this core's declared token list includes values that are
   broken through the mupen config bridge — but then the comment must say so, and the generator
   must respect it.)
4. **Deliberate hand-curation exists and must survive regeneration.** melondsds/citra expose ONE
   lever by policy (render-mode keys are load-bearing pins); scummvm's exclusions are documented
   line-by-line in the C# file; parallel_n64's curation encodes bridge-broken tokens. The Phase 1
   generator therefore needs a per-core policy (exclude / hand-only / fold-in), or regeneration
   bulldozes decisions that took sessions to earn. Hand entries already win over extraction
   (`AddExtracted` skips existing keys) — the policy list covers whole-core exclusions.

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

### Phase 1 — establish ground truth about the deployed cores (kills D4, half of D7)

Nothing else is trustworthy until this exists.

1. Write the generator the catalog header has always assumed: a **runtime harness**, not a static
   parser. A tiny loader (`scripts/extract-core-options/` — C# is fine) `LoadLibrary`s each DLL in
   `D:\ArcadeStorage\worker-gl\assets\cores` (**what is deployed**, not stock), calls
   `retro_set_environment` with a callback that answers `GET_CORE_OPTIONS_VERSION` and captures
   `SET_CORE_OPTIONS_V2` / `SET_CORE_OPTIONS` / `SET_VARIABLES`, and emits the catalog JSON. This
   sidesteps the pointer-array/linker-pooling trap entirely (the withdrawn D4 claim) because the
   core itself hands over the real structs. Static struct-walking is the fallback if a specific
   core won't survive a bare `retro_set_environment` (crash-isolate per core: one child process
   each, a crasher is recorded and skipped, never taken as "no options"). Skip `*.pre-*.dll`
   backups; dedupe stock vs `_custom` by what the config actually loads (`lib:` per system).
2. **Run it over the whole deployed dir**, not the 15 known cores — this is what closes D7.1.
   Per-core policy file (committed next to the generator): `fold` (default), `hand-only`
   (parallel_n64 — bridge-broken tokens; melondsds/citra/scummvm — policy curation), with the
   reason recorded per entry. Fix the stale parallel_n64 comment to match.
3. Diff old vs new and write the drift report into the repo (docs/arcade/). This answers the
   pcsx2 `OpenGL`/`Auto`/`Vulkan` renderer-token question with evidence, and shows what the
   custom builds (`pcsx2_custom`, `ppsspp_custom`, `flycast_custom`, `dolphin_custom`,
   `citra_custom`, patched mupen/parallel_n64) really declare — including MT-patched options.
4. Add the missing `SystemDefaultCore` mappings (D7.2: sg1000 + the 17 small systems) so every
   system with a catalogued core gets its ⚙ button. Small-system option sets are naturally small;
   the derived Save bound already scales.
5. Commit the generator + policy so a re-run is reproducible, reviewable, and a no-op when nothing
   drifted.

**Exit:** the catalog provably matches the deployed cores, every deployed core is either catalogued
or policy-excluded with a reason, and a re-run is a no-op.

### Phase 2 — model the renderer half (kills D3)

1. Add applicability to `CoreOption` — keyed by **render-profile id**, not by `HwContext`.
   ⚠ Surface is too coarse: parallel_n64 has TWO gl-surface profiles (`parallel_n64_gl` =
   GLideN64, `parallel_n64_glide64` = Glide64) and the `parallel-n64-gliden64-*` keys are live on
   exactly one of them. Model: `CoreOption.Profiles` = null (live everywhere the core runs — the
   default, and the only sane state for 2D/software cores) or a set of profile ids. Systems with
   no profiles (saturn, nds, 3ds, all 2D) never filter.
2. Populate it empirically — and **mine the existing worker logs first**. The fork already ships
   the instrument: `[opt] DEAD keys (provided but core NEVER queried — value INERT)` and
   `[opt] reconcile: M/N read`, and weeks of glworker logs already contain those lines for every
   room ever booted. **DONE 2026-08-02 — `docs/arcade/opt-reconcile-evidence-2026-08-02.md`**
   (340 reconcile events across both workers). Headlines: ps2/paraLLEl-GS leaves exactly
   `pcsx2_upscale_multiplier`, `pcsx2_anisotropic_filtering`, `pcsx2_blending_accuracy` DEAD every
   time (the GSdx-only claim, now proven); mupen+parallel leaves only `169screensize` DEAD while
   mupen+gliden64 kills the `parallel-rdp-upscaling` knob plus (under the full superset) 9 FB/AA
   keys; parallel_n64+gliden64 leaves its own `parallel-n64-gliden64-*` FB keys unread (5/11 —
   needs re-confirmation on current builds; samples predate the 07-29 core updates); dolphin,
   flycast, citra, scummvm reconcile clean; kronos leaves `kronos_sh2coretype` DEAD. No-evidence
   combinations (must be booted via test-roms): naomi/atomiswave with options, every non-default
   renderer profile (ps2 opengl, beetle GL, the GL escapes on psp/dc/gc/wii).
   ⚠ "not queried in the sample window" ≠ "not applicable"; anything ambiguous stays VISIBLE and
   gets flagged for a longer run rather than hidden on weak evidence. Record the evidence source
   (log date/room) per restriction — an applicability entry with no evidence is a guess.
   **Two delivery-layer bleeds surfaced by the sweep** (worker/yaml-side, NOT this module — inert
   but they pollute every reconcile and make room configs unreadable; fix opportunistically,
   separately from this plan): (a) pcsx_rearmed rooms are handed `beetle_psx_hw_renderer=
   "hardware_vk"` (the worker's beetle-only W3 hwctx pin fires on system=ps1 regardless of core);
   (b) parallel_n64 rooms receive `mupen64plus-*`-prefixed keys (`AllowUnalignedDMA`, `CountPerOp`,
   `pak1..4`) that its native `parallel-n64-*` namespace never reads.
3. Filter `GetGameConfig` by the selected profile so only live options render (the UI already
   re-fetches per profile — `switchProfile` — so this is server-side only). The Advanced-set and
   Save's other-core-preservation logic must keep using the UNfiltered per-core key sets, or a
   filtered-out key would be misfiled as "advanced" / dropped on switch.
4. Split the blended baselines per profile: `UltraLiveSpec["pcsx2"]` mixes paraLLEl-GS (`pgs_*`)
   and GSdx (`upscale_multiplier`/`anisotropic`/`blending`) keys, so the modal's baseline is a
   blend and switching renderers can't change displayed values even with D2 fixed. With
   applicability in place the display fix can be exactly that filter (baseline overlays only
   applicable keys); the yaml WELD test keeps asserting the union the yaml delivers.

**Exit:** switching Graphics visibly changes the option set, and no shown option is inert.

### Phase 3 — fix the PS2 profiles (D6)

Offer the renderers that are real, and only ones that have been booted:

- **paraLLEl-GS** (`hwContext: vulkan`) — today's default, keeps the `pgs_*` levers. Relabel so
  "Vulkan" stops meaning paraLLEl-GS.
- **Vulkan (GSdx)** (`hwContext: vulkan`) — PCSX2's native backend, where the GameDB hw-fixes and
  `upscale_multiplier`/`anisotropic`/`blending` apply. Currently unreachable from the site. Token
  name comes from Phase 1's evidence, never guessed.
- **`opengl`**: decide by boot test, not by flag. The per-room `hwctx=gl` override is the designed
  W3-F1 escape, so it *may* work despite `isGlAllowed: false` — but it has never once been
  exercised. If it boots, streams, and zero-copies under test-roms, keep it (relabeled "OpenGL
  (GSdx)"); if not, retire it.

Verify every survivor actually boots and streams (`test-roms`) before it is offered; a renderer
that cannot zero-copy into the capture path must not appear in the list. The same
verify-before-offer bar applies fleet-wide to the six surface-only systems' GL profiles
(psp/dc/naomi/atomiswave/gc/wii) — same never-exercised shape, same test, lower stakes.

**Exit:** every offered PS2 profile has been booted and streamed at least once.

### Phase 4 — close the loop so this cannot recur

1. **Test:** every `RenderProfile.Options` key/token validates against the catalog (closes D5 — the
   guard that would have caught a retired renderer token). Renderer-selecting keys are absent from
   the catalog *by design*, so the test validates them against the extraction's raw output (the
   generator emits renderer keys into a sidecar section for exactly this) — not by re-adding them
   to the module's catalog.
2. **Test:** every option a profile can show is live for that profile (closes D3 structurally), and
   every applicability restriction cites evidence.
3. **Drift check:** re-run the Phase 1 extractor against the deployed cores and fail on difference.
   This can only run where the DLLs exist — the self-hosted runner on Ziggy, or a scheduled local
   task that files an alert — not the GitHub-hosted lane.
4. **Data cleanup:** dry-run sweep of **all** `IsRendererSelecting` keys out of **all**
   `ArcadeGameProfile.CoreOptionsJson` rows (live-DB audit 2026-08-02: **16** ps2 rows carry
   `pcsx2_renderer`; zero strays on any other system; 3 n64 rows hold legitimate RenderProfile
   pins), then `arcade-gameconfig-export`. Until this runs, `game-overrides.json`
   still ships the key and the selector stays overridden for those titles *even with Phase 0
   deployed*.

---

## 4. The fleet (audited 2026-08-02 — what Phase 2 must cover)

Every (system → profile → core) the site can boot. "Applicability risk" = whether options shown for
the system can be inert under some profile.

### Systems with a Graphics selector (`ArcadeRendererProfiles.BySystem`)

| system | profiles (first = default) | OptionCore | applicability risk |
|---|---|---|---|
| n64 | mupen `vulkan` / mupen `opengl` / `parallel_n64` (vk) / `parallel_n64_gl` (GLideN64) / `parallel_n64_glide64` | mupen64plus_next OR parallel_n64 | **highest** — 2 cores × plugin-specific keys; `parallel-rdp-*` vk-only, `gliden64-*` one GL profile only, `gfxplugin-accuracy` GL-only |
| ps2 | `vulkan` (=paraLLEl-GS) / `opengl` | pcsx2 | **high** — `pgs_*` vs GSdx keys (the headline D3); profile set itself wrong (D6) |
| ps1 | `beetle_vulkan` / `beetle_opengl` / `pcsx_rearmed` | beetle_psx_hw OR pcsx_rearmed | medium — core split already modelled by OptionCore; Beetle vk-vs-gl-only keys need evidence |
| psp / dc / naomi / atomiswave / gc / wii | `vulkan` / `opengl` (surface-only) | ppsspp / flycast / dolphin | medium — one core, but backend-specific keys (e.g. flycast OIT) may be surface-dependent; GL profiles never exercised since the Vulkan cutover |

### Systems with ONE fixed core+renderer (no selector — never filter)

saturn (kronos, GL), nds (melondsds, GL — hand-curated single lever), 3ds (citra_custom, GL —
hand-curated single lever), scummvm (hand-curated, software), dos (dosbox_pure), and every 2D/
software system: nes fds snes gb gbc gba genesis sms gg sg1000 segacd sega32x arcade neogeo pce
ngpc wsc a2600 a7800 lynx vb vectrex intv coleco channelf o2em arcadia supervision pokemini 3do cdi.

### Catalog coverage today

- **Extraction JSON (15):** flycast dolphin kronos ppsspp pcsx2 mgba mupen64plus_next pcsx_rearmed
  beetle_psx_hw snes9x nestopia genesis_plus_gx picodrive fbneo dosbox_pure.
- **Hand-only (deliberate):** parallel_n64, melondsds, citra, scummvm.
- **Uncovered deployed cores (D7.1):** opera, same_cdi, stella, prosystem, mednafen_pce, mednafen_ngp,
  mednafen_wswan, mednafen_lynx, mednafen_vb, vecx, freeintv, gearcoleco, freechaf, o2em, amiarcadia,
  potator, pokemini.
- **Unmapped systems (D7.2):** sg1000 pce ngpc wsc a2600 a7800 lynx vb vectrex intv coleco channelf
  o2em arcadia supervision pokemini 3do cdi.
- **Out of scope:** the capture lane (zone=capture) — not libretro; its per-title config is
  JSON/game-overrides on the capture worker, not this module.

## 5. Standing rule this establishes

> A player-facing option list is a claim that every entry does something. Validate **across** layers —
> deployed DLL → catalog → profile → rendered option — or the claim rots silently, because libretro
> never errors on a key or token it does not recognise.
