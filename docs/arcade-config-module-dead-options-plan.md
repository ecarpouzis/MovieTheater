# Arcade per-game config module — dead options, stale catalog, inert renderer selector

**Status: ALL PHASES SHIPPED 2026-08-02.** Phase 0 `ef62f05`, Phase 1 `c5cfe5b`, Phase 2 `2872d72`,
Phase 3 `fb3cb5a` (all three PS2 renderers boot-verified — see D6 and Phase 3 below), Phase 4 in
the closing commit. 394 tests green.
Phase 4 closed: `ArcadeRenderProfileValidationTests` validates every profile key/token against the
deployed DLLs' own declarations (D5 — via the catalog + the `rendererKeys` sidecar + a documented
6-key hand-verified allowlist for the hand-only parallel_n64 GL FB set);
`scripts/check-core-options-drift.ps1` re-extracts the deployed cores and fails on drift, proven
end-to-end (exit 0 same-day; it is Ziggy-local because the CI runner is not this machine — schtasks
registration one-liner in its header, deliberately left to a human); and the **data sweep ran**:
16 ps2 rows dropped `pcsx2_renderer` (guarded UPDATE, safety net `_cfgmod_renderer_sweep_20260802`,
0 renderer-selecting keys remain fleet-wide), with the re-exported `game-overrides.json` deployed
to both GL ConfDirs (`.pre-renderer-sweep-20260802` backups beside them).
Open beyond this plan's scope: the six surface-only systems' GL profiles remain unverified (same
verify-before-offer bar as ps2, lower stakes); `pcsx2_pgs_deblur`/`_ss_tex` applicability is
structural-only (never provided in any room); two delivery-layer bleeds (§3 Phase 2.2) fix
separately.
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

⚠ *Corrected by the Phase 3 boot tests:* `pgs_disable_mipmaps` does **not** belong in the first row —
LRPS2 reads it under paraLLEl-GS *and* both GSdx backends. The row was written from the key's name;
the name is not the boundary.

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

### D6 — The PS2 profile set is wrong on the merits *(FIXED, Phase 3)*

**Was** (two profiles, one of them mislabelled, one never exercised):

| profile | label | sets | HwContext |
|---|---|---|---|
| `vulkan` | "Vulkan (paraLLEl-GS)" | `pcsx2_renderer=paraLLEl-GS` | `vulkan` |
| `opengl` | "OpenGL" | `pcsx2_renderer=OpenGL` | `gl` |

**Now** (three profiles, every one booted and streamed on 2026-08-02 before being offered —
`docs/arcade/opt-reconcile-evidence-2026-08-02.md`, "Phase 3 boot tests"):

| profile | label | sets | HwContext | boot evidence | live levers |
|---|---|---|---|---|---|
| `parallel_gs` *(default)* | "paraLLEl-GS (Vulkan)" | `pcsx2_renderer=paraLLEl-GS` | `vulkan` | 13:35:17, zero-copy ACTIVE, reconcile **6/9** | `pcsx2_pgs_ssaa`, `pcsx2_pgs_high_res_scanout` |
| `vulkan_gsdx` **(new)** | "Vulkan (GSdx)" | `pcsx2_renderer=Vulkan` | `vulkan` | 13:29:09, zero-copy ACTIVE, 59–60 fps / 0 freezes, reconcile **7/9** | `pcsx2_upscale_multiplier`, `pcsx2_anisotropic_filtering`, `pcsx2_blending_accuracy` |
| `opengl` *(kept, relabelled)* | "OpenGL (GSdx)" | `pcsx2_renderer=OpenGL` | `gl` | 13:32:24, `Created an OpenGL context`, flat 60 fps / 0 freezes, reconcile **7/9** | same as `vulkan_gsdx` |

`pcsx2_pgs_disable_mipmaps` is read by **all three** and stays visible everywhere — the boot tests'
one surprise, and the reason the `pcsx2_pgs_` prefix rule now carries an explicit exception.

The two original problems, and how they were settled:

1. **The label lied.** `Vulkan` and `paraLLEl-GS` are *different GS implementations* that both run on
   a Vulkan surface. The profile called "Vulkan" selected paraLLEl-GS. PCSX2's own Vulkan (GSdx)
   backend was not exposed by the site at all. ✅ **Settled:** the label now names the GS
   implementation, and Vulkan (GSdx) is a profile of its own — reached in the boot test through a
   temporary `CoreOptionsJson` row (the D2 merge order is what made an unreachable renderer testable
   at all), then verified to zero-copy and stream before being offered.
   ⚠ *One claim did NOT survive:* "where the GameDB hardware fixes apply" is **not** shown by the
   test. GameDB fixes are logged on the paraLLEl-GS path too (Stuntman, 12:24:20:
   `Enabled GS Hardware Fix: cpuSpriteRenderBW/halfPixelOffset`), and the test title matched no fix
   at all. The GSdx profile is justified by the levers that provably work there, not by GameDB.
2. **The GL profile was a pre-Vulkan leftover that had NEVER been exercised.** `config.worker-gl.yaml`'s
   ps2 block is `isGlAllowed: false` + `hwContext: "vulkan"`, and every glworker log showed only
   paraLLEl-GS. ⚠ *Correction (second pass):* `isGlAllowed: false` does **not** make the profile
   impossible — the per-room `hwctx=gl` override is the **designed** W3-F1 GL escape (the yaml's own
   comments on psp/dc/gc say exactly this: "a per-GAME GL escape must use the explicit
   hwContext:'gl' override field"). ✅ **Settled: KEPT.** The escape works for ps2 and the worker says
   so verbatim — `Per-game hw context: … → "gl" (core default "vulkan", via per-request override)`,
   then `Created an OpenGL context` with real GL entry points, no `rejected non-GL hw render context
   type`, 70 s at a flat 60 fps with 0 freezes and `pace-diag ticks/s=59.9 slowTicks=0`. It logs no
   zero-copy line, correctly: zero-copy here is the *Vulkan→GL import* path, and a core already
   rendering into the worker's GL context has nothing to import.

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

### Phase 2 — model the renderer half (kills D3) ✅ *shipped*

> **Shipped 2026-08-02** as `ArcadeCoreOptionApplicability` (a hand-curated rule map keyed by render-profile
> id, every rule carrying a mandatory evidence string) + server-side filtering in `GetGameConfig`, the
> displayed baseline, and the tier-preset apply path. Restrictions taken: pcsx2's three GSdx keys → the GSdx
> profile (worker-log DEAD in every sample), `pcsx2_pgs_*` → paraLLEl-GS (structural namespace);
> mupen64plus_next's `parallel-rdp-*` → vulkan and the 8 GLideN64 FB/AA keys → opengl; parallel_n64's
> `parallel-rdp-upscaling` → vulkan, `gliden64-*` → `parallel_n64_gl` (latent — hand-only core),
> `gfxplugin-accuracy` → both GL profiles. Left VISIBLE for want of evidence: `mupen64plus-169screensize`
> (dead under both plugins = aspect-dependent, wrong axis), `parallel-n64-screensize` (strong candidate,
> only our own note backs it), pcsx2's other GSdx-ish keys, and every beetle/ppsspp/flycast/dolphin key.
> The save path gained `MergeSave`, which preserves saved keys of the SELECTED core that this profile does
> not render — without it the first Save under one profile would silently delete the other's tuning, because
> the modal posts the full RENDERED set. Zero UI changes (the modal already re-fetches per profile).
>
> ⚠ *Amended by Phase 3 (2026-08-02).* Two of the restrictions taken here were later re-grounded by
> boot evidence: the pcsx2 GSdx keys gained their missing LIVE half (they are read on both GSdx
> profiles, not merely dead on paraLLEl-GS), and the `pcsx2_pgs_` prefix rule — taken purely on the
> structural namespace argument, because the site had never sent a `pgs_*` key — turned out to be
> **wrong for one key**: `pcsx2_pgs_disable_mipmaps` is read by every renderer. The prefix rule
> survives with an explicit exception. Everything else held.

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

### Phase 3 — fix the PS2 profiles (D6) ✅ *shipped 2026-08-02*

> **Shipped.** PS2 now offers **three** profiles, each booted and streamed on the deployed site
> before being offered — `parallel_gs` ("paraLLEl-GS (Vulkan)", default), `vulkan_gsdx`
> ("Vulkan (GSdx)", NEW) and `opengl` ("OpenGL (GSdx)", kept on boot evidence). Full log evidence:
> `docs/arcade/opt-reconcile-evidence-2026-08-02.md` → "Appendix — Phase 3 boot tests". Renaming the
> ids was DB-safe: the live audit found zero ps2 `ArcadeGameProfile` rows with `RenderProfile` set,
> and an unknown saved id already falls back to the system default.

What the boots proved, and what changed because of them:

1. **The three arms are an exact mirror.** One game (Persona 3 FES), one identical 9-key provided
   set, three renderers, one worker: paraLLEl-GS **6/9** (DEAD: upscale/aniso/blending),
   Vulkan (GSdx) **7/9** and OpenGL (GSdx) **7/9** (DEAD: `pgs_high_res_scanout`, `pgs_ssaa`).
   The GSdx half of D3 had never been measured before — only the paraLLEl-GS half.
2. **The two GSdx profiles are indistinguishable at the option level.** Same reconcile, same DEAD
   set; only the surface differs. So they share applicability rules and share one preset bundle.
3. **`pcsx2_pgs_disable_mipmaps` is read by all three.** The Phase 2 `pcsx2_pgs_` prefix rule was
   taken on a purely structural namespace argument, and this falsifies it for one key — the rule now
   carries an explicit exception. Recorded as the standing lesson: a namespace prefix is a
   hypothesis, not a boundary.
4. **⚠ The preset trap, and how it was closed.** `ArcadeQualityPresets` was keyed `(core, hwContext)`,
   and `vulkan_gsdx` shares hwContext `vulkan` with `parallel_gs`. A GSdx tier reset would therefore
   have fetched the *paraLLEl-flavoured* bundle, whose every key the controller's apply-time
   applicability filter then strips — storing **nothing at all**. "Reset to Max" would have been a
   silently dead button, on the one path that deliberately skips the baseline-drop. Fixed by keying
   pcsx2's presets by **render-profile id**: `For()` now resolves profile id → hwContext → core-wide,
   so only the cores that need the finer key pay for it. The GSdx bundle written for the GL profile
   is *reused verbatim* for `vulkan_gsdx` (finding 2 is what licenses that), and two new tests pin it:
   every ps2 profile's tier bundle survives its own room's applicability filter, and the paraLLEl-GS
   and GSdx bundles share no key in either direction.
5. **No UI change was needed.** The play-button menu has enumerated `ArcadeRendererProfiles` since the
   2026-07-21 GameModal, so all three profiles appear with honest labels and nothing dead is offered.
   `ForRenderer` still resolves the bare fallbacks (`vulkan` → `parallel_gs`, `gl` → `opengl`), which
   only matter before the profile list loads.

**Deferred, unchanged:** the same verify-before-offer bar still applies fleet-wide to the six
surface-only systems' GL profiles (psp/dc/naomi/atomiswave/gc/wii) — same never-exercised shape, same
test, lower stakes. Phase 3 covered PS2 only.

**Exit met:** every offered PS2 profile has been booted and streamed at least once.

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
| ps2 | `parallel_gs` (paraLLEl-GS) / `vulkan_gsdx` (GSdx, Vulkan) / `opengl` (GSdx, GL) | pcsx2 | **high** — `pgs_*` vs GSdx keys (the headline D3), all three now boot-measured; two profiles SHARE hwContext `vulkan`, so neither applicability nor the presets can key on surface (D6 fixed, Phase 3) |
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

And the corollary Phase 3 added, paid for by one boot test:

> **A key's PREFIX is a hypothesis about which implementation reads it, not a boundary.**
> `pcsx2_pgs_disable_mipmaps` is named for paraLLEl-GS and read by every PS2 renderer. Namespace
> reasoning is a fine way to *decide what to measure*; it is not a substitute for measuring. Where a
> restriction rests on a name alone, say so in its evidence string and treat it as provisional.

> **The cheapest way to reach an unreachable renderer is the defect that made it unreachable.** The
> Vulkan (GSdx) arm was booted through the very saved-options-win merge (D2) that the plan exists to
> fix — a temporary `CoreOptionsJson` row. Before removing a bad precedence rule, check whether it is
> currently the only test harness you have for the layer above it.
