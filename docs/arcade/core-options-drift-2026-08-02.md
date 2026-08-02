# Arcade core-option drift report — 2026-08-02

**Generated** by `scripts/extract-core-options.ps1` (runtime harness, one child process per DLL)
against the DEPLOYED cores. Regenerate with:

```powershell
pwsh -File scripts/extract-core-options.ps1 -Force
```

Method: each DLL is `LoadLibrary`d in its own process and handed a `retro_environment_t` that
answers `GET_CORE_OPTIONS_VERSION` = 2 and captures `SET_VARIABLES` (16) / `SET_CORE_OPTIONS` (53) /
`_INTL` (54) / `SET_CORE_OPTIONS_V2` (67) / `_V2_INTL` (68). The core hands over the real structs, so
this sidesteps the linker string-pooling trap that made the earlier static read of `pcsx2_renderer`
wrong. A crash or timeout is RECORDED as such and never read as "this core has no options".

## 1. Extraction outcome — every deployed DLL

| dll | core key | in config `lib:` | outcome | source | options | library (version) |
|---|---|---|---|---|---:|---|
| `amiarcadia_libretro.dll` | `amiarcadia` | yes | **ok** | `SET_VARIABLES` | 3 | AmiArcadia (4.60) |
| `mednafen_psx_hw_libretro.dll` | `beetle_psx_hw` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 81 | Beetle PSX HW (0.9.44.1 df25987) |
| `citra_custom_libretro.dll` | `citra` | yes | **ok** | `SET_VARIABLES` | 27 | Citra (e3e057f-dirty) |
| `citra_libretro.dll` | `citra` | no | **ok** | `SET_VARIABLES` | 26 | Citra (e3e057f) |
| `dolphin_custom_libretro.dll` | `dolphin` | yes | **no-options-before-content** | — | 0 | dolphin-emu (287ab2d.0.0+287ab2dba6) |
| `dolphin_libretro.dll` | `dolphin` | no | **no-options-before-content** | — | 0 | dolphin-emu (2606.0.88+287ab2dba6) |
| `dosbox_pure_libretro.dll` | `dosbox_pure` | stock default | **ok-after-retro_init** | `SET_CORE_OPTIONS_V2` | 48 | DOSBox-pure (1.0-preview5) |
| `fbneo_libretro.dll` | `fbneo` | yes | **no-options-before-content** | — | 0 | FinalBurn Neo (v1.0.0.03 260703 GITc7b89b7) |
| `flycast_custom_libretro.dll` | `flycast` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 89 | Flycast (f09d1f2) |
| `flycast_libretro.dll` | `flycast` | no | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 88 | Flycast (324d9c1) |
| `freechaf_libretro.dll` | `freechaf` | yes | **ok** | `SET_VARIABLES` | 1 | FreeChaF (1.0 76c7a84) |
| `freeintv_libretro.dll` | `freeintv` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 2 | freeintv (1.2  428915b) |
| `gearcoleco_libretro.dll` | `gearcoleco` | yes | **ok** | `SET_CORE_OPTIONS_V2` | 7 | Gearcoleco (1.6.7) |
| `genesis_plus_gx_libretro.dll` | `genesis_plus_gx` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 62 | Genesis Plus GX (v1.7.4 accdd6e) |
| `kronos_libretro.dll` | `kronos` | yes | **ok** | `SET_CORE_OPTIONS_INTL` | 17 | Kronos (v2.7.0 6709c1d) |
| `mednafen_lynx_libretro.dll` | `mednafen_lynx` | yes | **ok-after-retro_init** | `SET_CORE_OPTIONS_V2` | 3 | Beetle Lynx (v1.24.0 fcdefcf) |
| `mednafen_ngp_libretro.dll` | `mednafen_ngp` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 1 | Beetle NeoPop (v1.29.0.0 a50d5ac) |
| `mednafen_pce_libretro.dll` | `mednafen_pce` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 42 | Beetle PCE (v1.29.0 ae99235) |
| `mednafen_vb_libretro.dll` | `mednafen_vb` | yes | **ok** | `SET_CORE_OPTIONS_INTL` | 7 | Beetle VB (v1.31.0 38e7a0e) |
| `mednafen_wswan_libretro.dll` | `mednafen_wswan` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 9 | Beetle WonderSwan (v0.9.35.1 da6d0d9) |
| `melonds_libretro.dll` | `melonds` | stock default | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 25 | melonDS (0.9.3 66b5d26) |
| `melondsds_libretro.dll` | `melondsds` | yes | **no-options-before-content** | — | 0 | melonDS DS (1.2.0) |
| `mgba_libretro.dll` | `mgba` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 17 | mGBA (0.11-dev 6dce57e) |
| `mupen64plus_next_libretro.dll` | `mupen64plus_next` | yes | **ok** | `SET_CORE_OPTIONS_V2` | 86 | Mupen64Plus-Next (2.8-Vulkan 98c1b0d) |
| `nestopia_libretro.dll` | `nestopia` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 34 | Nestopia (1.53.2 b0fd87d) |
| `o2em_libretro.dll` | `o2em` | yes | **ok** | `SET_CORE_OPTIONS_INTL` | 10 | O2EM (1.18 e03d3be) |
| `opera_libretro.dll` | `opera` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 17 | Opera (1.0.0 5a4eb96) |
| `parallel_n64_libretro.dll` | `parallel_n64` | yes | **ok** | `SET_CORE_OPTIONS_V2` | 94 | ParaLLEl N64 (1.0 3981986) |
| `pcsx2_custom_libretro.dll` | `pcsx2` | yes | **ok-after-retro_init** | `SET_CORE_OPTIONS_V2_INTL` | 64 | LRPS2 (v2.0.0-fe939ae) |
| `pcsx2_libretro.dll` | `pcsx2` | no | **ok-after-retro_init** | `SET_CORE_OPTIONS_V2_INTL` | 64 | LRPS2 (v2.0.0-b03969a) |
| `pcsx_rearmed_libretro.dll` | `pcsx_rearmed` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 53 | PCSX-ReARMed (r26 050981b) |
| `picodrive_libretro.dll` | `picodrive` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 21 | PicoDrive (2.05-f0d4a01) |
| `pokemini_libretro.dll` | `pokemini` | yes | **ok** | `SET_CORE_OPTIONS_INTL` | 13 | PokeMini (v0.60) |
| `potator_libretro.dll` | `potator` | yes | **ok** | `SET_CORE_OPTIONS_INTL` | 4 | Potator (1.0.5  227c5f6) |
| `ppsspp_custom_libretro.dll` | `ppsspp` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 75 | PPSSPP (dbba810) |
| `ppsspp_libretro.dll` | `ppsspp` | no | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 75 | PPSSPP (56c694d) |
| `prosystem_libretro.dll` | `prosystem` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 4 | ProSystem (1.3e 363b6df) |
| `same_cdi_libretro.dll` | `same_cdi` | yes | **ok** | `SET_VARIABLES` | 16 | SAME_CDI (0.239) |
| `scummvm_libretro.dll` | `scummvm` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 36 | ScummVM (660e13b0-2026.2.1git) |
| `snes9x_libretro.dll` | `snes9x` | stock default | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 40 | Snes9x (1.63 185488c) |
| `stella_libretro.dll` | `stella` | yes | **ok** | `SET_CORE_OPTIONS_V2` | 17 | Stella (8.0_pre b52ccb0) |
| `vecx_libretro.dll` | `vecx` | yes | **ok** | `SET_VARIABLES` | 12 | VecX (1.2 8f671cc) |

⚠ Not clean — see the non-`ok` rows above:
- `dolphin_custom_libretro.dll`: **no-options-before-content** — dep dirs on PATH: C:\msys64\ucrt64\bin;C:\msys64\mingw64\bin; no options from retro_set_environment; trying retro_init
- `dolphin_libretro.dll`: **no-options-before-content** — dep dirs on PATH: C:\msys64\ucrt64\bin;C:\msys64\mingw64\bin; no options from retro_set_environment; trying retro_init
- `fbneo_libretro.dll`: **no-options-before-content** — dep dirs on PATH: C:\msys64\ucrt64\bin;C:\msys64\mingw64\bin; no options from retro_set_environment; trying retro_init
- `melondsds_libretro.dll`: **no-options-before-content** — dep dirs on PATH: C:\msys64\ucrt64\bin;C:\msys64\mingw64\bin; no options from retro_set_environment; trying retro_init

## 2. Renderer tokens the DEPLOYED cores really declare (answers the withdrawn D4 claim)

Evidence, not inference: these are the value arrays the cores themselves passed to the frontend.

### `pcsx2_renderer`

- `pcsx2_custom_libretro.dll` *(custom build)* — default `Auto`, 8 tokens:
  `Auto` · `OpenGL` · `D3D11` · `D3D12` · `Vulkan` · `paraLLEl-GS` · `Software (HW)` · `Software (SW)`
- `pcsx2_libretro.dll` — default `Auto`, 8 tokens:
  `Auto` · `OpenGL` · `D3D11` · `D3D12` · `Vulkan` · `paraLLEl-GS` · `Software (HW)` · `Software (SW)`

### `beetle_psx_hw_renderer`

- `mednafen_psx_hw_libretro.dll` — default `hardware`, 4 tokens:
  `hardware` · `hardware_gl` · `hardware_vk` · `software`

### `mupen64plus-rdp-plugin`

- `mupen64plus_next_libretro.dll` — default `gliden64`, 3 tokens:
  `angrylion` · `parallel` · `gliden64`

### `mupen64plus-rsp-plugin`

- `mupen64plus_next_libretro.dll` — default `hle`, 3 tokens:
  `cxd4` · `parallel` · `hle`

### `parallel-n64-gfxplugin`

- `parallel_n64_libretro.dll` — default `gliden64`, 6 tokens:
  `gliden64` · `glide64` · `gln64` · `rice` · `angrylion` · `parallel`

### `parallel-n64-rspplugin`

- `parallel_n64_libretro.dll` — default `auto`, 4 tokens:
  `auto` · `hle` · `cxd4` · `parallel`

## 3. Stock vs `_custom` deltas

### `citra` — catalog uses `citra_custom_libretro.dll`

vs `citra_libretro.dll` (26 options, outcome ok):

- only on the deployed build (1): `citra_graphics_api`
- only on the other build (0): —
- different token lists (0): —
- different defaults (0): 

### `dolphin` — catalog uses `dolphin_custom_libretro.dll`

vs `dolphin_libretro.dll` (0 options, outcome no-options-before-content):

- only on the deployed build (0): —
- only on the other build (0): —
- different token lists (0): —
- different defaults (0): 

### `flycast` — catalog uses `flycast_custom_libretro.dll`

vs `flycast_libretro.dll` (88 options, outcome ok):

- only on the deployed build (1): `reicast_coin_limit`
- only on the other build (0): —
- different token lists (0): —
- different defaults (0): 

### `pcsx2` — catalog uses `pcsx2_custom_libretro.dll`

vs `pcsx2_libretro.dll` (64 options, outcome ok-after-retro_init):

- only on the deployed build (0): —
- only on the other build (0): —
- different token lists (0): —
- different defaults (0): 

### `ppsspp` — catalog uses `ppsspp_custom_libretro.dll`

vs `ppsspp_libretro.dll` (75 options, outcome ok):

- only on the deployed build (0): —
- only on the other build (0): —
- different token lists (0): —
- different defaults (0): 

## 4. Catalog diff — old committed JSON vs this extraction

| core | disposition | old | new | +added | -removed | default changed | tokens changed |
|---|---|---:|---:|---:|---:|---:|---:|
| `amiarcadia` | fold | — | 3 | 3 | 0 | 0 | 0 |
| `beetle_psx_hw` | fold | 81 | 81 | 0 | 0 | 0 | 2 |
| `citra` | hand-only | — | — | 0 | 0 | 0 | 0 |
| `dolphin` | fold | 99 | 0 | 0 | 0 | 0 | 0 |
| `dosbox_pure` | fold | 48 | 48 | 0 | 0 | 1 | 1 |
| `fbneo` | fold | 41 | 0 | 0 | 0 | 0 | 0 |
| `flycast` | fold | 89 | 89 | 0 | 0 | 0 | 0 |
| `freechaf` | fold | — | 1 | 1 | 0 | 0 | 0 |
| `freeintv` | fold | — | 2 | 2 | 0 | 0 | 0 |
| `gearcoleco` | fold | — | 7 | 7 | 0 | 0 | 0 |
| `genesis_plus_gx` | fold | 62 | 62 | 0 | 0 | 1 | 0 |
| `kronos` | fold | 17 | 17 | 0 | 0 | 0 | 0 |
| `mednafen_lynx` | fold | — | 3 | 3 | 0 | 0 | 0 |
| `mednafen_ngp` | fold | — | 1 | 1 | 0 | 0 | 0 |
| `mednafen_pce` | fold | — | 42 | 42 | 0 | 0 | 0 |
| `mednafen_vb` | fold | — | 7 | 7 | 0 | 0 | 0 |
| `mednafen_wswan` | fold | — | 9 | 9 | 0 | 0 | 0 |
| `melonds` | fold | — | 25 | 25 | 0 | 0 | 0 |
| `melondsds` | hand-only | — | — | 0 | 0 | 0 | 0 |
| `mgba` | fold | 13 | 17 | 4 | 0 | 1 | 2 |
| `mupen64plus_next` | fold | 86 | 86 | 0 | 0 | 1 | 2 |
| `nestopia` | fold | 36 | 34 | 0 | 2 | 0 | 1 |
| `o2em` | fold | — | 10 | 10 | 0 | 0 | 0 |
| `opera` | fold | — | 17 | 17 | 0 | 0 | 0 |
| `parallel_n64` | hand-only | — | — | 0 | 0 | 0 | 0 |
| `pcsx2` | fold | 64 | 64 | 0 | 0 | 1 | 0 |
| `pcsx_rearmed` | fold | 62 | 53 | 0 | 9 | 0 | 1 |
| `picodrive` | fold | 22 | 21 | 0 | 1 | 0 | 0 |
| `pokemini` | fold | — | 13 | 13 | 0 | 0 | 0 |
| `potator` | fold | — | 4 | 4 | 0 | 0 | 0 |
| `ppsspp` | fold | 75 | 75 | 0 | 0 | 0 | 0 |
| `prosystem` | fold | — | 4 | 4 | 0 | 0 | 0 |
| `same_cdi` | fold | — | 16 | 16 | 0 | 0 | 0 |
| `scummvm` | hand-only | — | — | 0 | 0 | 0 | 0 |
| `snes9x` | fold | 48 | 40 | 0 | 8 | 0 | 0 |
| `stella` | fold | — | 17 | 17 | 0 | 0 | 0 |
| `vecx` | fold | — | 12 | 12 | 0 | 0 | 0 |

### `amiarcadia`

- **NEW CORE** in the catalog (3 options) — closes a D7.1 gap.

### `beetle_psx_hw`

- **token lists RECOVERED** (6) — the old snapshot carried these with an EMPTY value list, so `ParseExtracted` dropped them and the config module never showed them at all: `beetle_psx_hw_cpu_freq_scale`, `beetle_psx_hw_mouse_sensitivity`, `beetle_psx_hw_memcard_left_index`, `beetle_psx_hw_memcard_right_index`, `beetle_psx_hw_dynarec_eventcycles`, `beetle_psx_hw_image_offset_cycles`
- changed token lists (2):
  - beetle_psx_hw_image_crop: +[1px, 2px, 3px, 4px, 5px, 6px, 7px, 8px, 9px, 10px, 11px, 12px, 13px, 14px, 15px, 16px, 17px, 18px, 19px, 20px]
  - beetle_psx_hw_image_offset: +[-12px, -11px, -10px, -9px, -8px, -7px, -6px, -5px, -4px, -3px, -2px, -1px, +1px, +2px, +3px, +4px, +5px, +6px, +7px, +8px, +9px, +10px, +11px, +12px]
- hand-authored ranges preserved: `beetle_psx_hw_initial_scanline`, `beetle_psx_hw_last_scanline`, `beetle_psx_hw_initial_scanline_pal`, `beetle_psx_hw_last_scanline_pal`

### `citra`

- ⚠ hand-only: not written to the catalog
- policy **hand-only**: Same single-lever policy as melondsds: only citra_resolution_factor is exposed. citra_graphics_api stays pinned to OpenGL because touch needs the GL MouseTracker. (extracted anyway: 27 options, outcome ok)

### `dolphin`

- ⚠ extraction no-options-before-content — OLD catalog block carried over verbatim

### `dosbox_pure`

- changed defaults (1):
  - dosbox_pure_midi: `disabled` -> `frontend`
- **token lists RECOVERED** (1) — the old snapshot carried these with an EMPTY value list, so `ParseExtracted` dropped them and the config module never showed them at all: `dosbox_pure_midi`
- changed token lists (1):
  - dosbox_pure_cpu_type: -[pentium_mmx]

### `fbneo`

- ⚠ extraction no-options-before-content — OLD catalog block carried over verbatim

### `flycast`

- **token lists RECOVERED** (1) — the old snapshot carried these with an EMPTY value list, so `ParseExtracted` dropped them and the config module never showed them at all: `reicast_coin_limit`
- label drift (old label KEPT for reviewability) (3):
  - reicast_per_content_vmus: `Per-Game Visual Memory Units/Systems (VMU)` (kept) vs core's `Per-Game VMUs`
  - reicast_vmu_sound: `Visual Memory Units/Systems (VMU) Sounds` (kept) vs core's `VMU Sounds`
  - reicast_show_vmu_screen_settings: `Show Visual Memory Unit/System (VMU) Display Settings` (kept) vs core's `Show VMU Display Settings`
- hand-authored ranges preserved: `reicast_sh4clock`

### `freechaf`

- **NEW CORE** in the catalog (1 options) — closes a D7.1 gap.

### `freeintv`

- **NEW CORE** in the catalog (2 options) — closes a D7.1 gap.

### `gearcoleco`

- **NEW CORE** in the catalog (7 options) — closes a D7.1 gap.

### `genesis_plus_gx`

- changed defaults (1):
  - genesis_plus_gx_psg_preamp: `100` -> `150`
- label drift (old label KEPT for reviewability) (10):
  - genesis_plus_gx_psg_channel_3_volume: `PSG Tone Channel 3 Volume %` (kept) vs core's `PSG Noise Channel 3 Volume %`
  - genesis_plus_gx_sms_fm_channel_0_volume: `Master System FM Channel 0 Volume %` (kept) vs core's `Master System FM (YM2413) Channel 0 Volume %`
  - genesis_plus_gx_sms_fm_channel_1_volume: `Master System FM Channel 1 Volume %` (kept) vs core's `Master System FM (YM2413) Channel 1 Volume %`
  - genesis_plus_gx_sms_fm_channel_2_volume: `Master System FM Channel 2 Volume %` (kept) vs core's `Master System FM (YM2413) Channel 2 Volume %`
  - genesis_plus_gx_sms_fm_channel_3_volume: `Master System FM Channel 3 Volume %` (kept) vs core's `Master System FM (YM2413) Channel 3 Volume %`
  - genesis_plus_gx_sms_fm_channel_4_volume: `Master System FM Channel 4 Volume %` (kept) vs core's `Master System FM (YM2413) Channel 4 Volume %`
  - genesis_plus_gx_sms_fm_channel_5_volume: `Master System FM Channel 5 Volume %` (kept) vs core's `Master System FM (YM2413) Channel 5 Volume %`
  - genesis_plus_gx_sms_fm_channel_6_volume: `Master System FM Channel 6 Volume %` (kept) vs core's `Master System FM (YM2413) Channel 6 Volume %`
  - genesis_plus_gx_sms_fm_channel_7_volume: `Master System FM Channel 7 Volume %` (kept) vs core's `Master System FM (YM2413) Channel 7 Volume %`
  - genesis_plus_gx_sms_fm_channel_8_volume: `Master System FM Channel 8 Volume %` (kept) vs core's `Master System FM (YM2413) Channel 8 Volume %`

### `mednafen_lynx`

- **NEW CORE** in the catalog (3 options) — closes a D7.1 gap.

### `mednafen_ngp`

- **NEW CORE** in the catalog (1 options) — closes a D7.1 gap.

### `mednafen_pce`

- **NEW CORE** in the catalog (42 options) — closes a D7.1 gap.

### `mednafen_vb`

- **NEW CORE** in the catalog (7 options) — closes a D7.1 gap.

### `mednafen_wswan`

- **NEW CORE** in the catalog (9 options) — closes a D7.1 gap.

### `melonds`

- **NEW CORE** in the catalog (25 options) — closes a D7.1 gap.

### `melondsds`

- ⚠ hand-only: not written to the catalog
- policy **hand-only**: Single-lever curation by policy: only melonds_opengl_resolution is exposed. melonds_render_mode is a load-bearing pin — the software renderer cannot upscale AND the stylus/touch path is wired for the GL frame, so folding the full set would offer a toggle that breaks both. (extracted anyway: 0 options, outcome no-options-before-content)

### `mgba`

- added keys (4): `mgba_color_correction`, `mgba_interframe_blending`, `mgba_frameskip_threshold`, `mgba_frameskip_interval`
- changed defaults (1):
  - mgba_frameskip: `0` -> `disabled`
- changed token lists (2):
  - mgba_gb_colors: +[DMG Green, GB Pocket, GB Light, GBC Brown ↑, GBC Red ↑A, GBC Dark Brown ↑B, GBC Pale Yellow ↓, GBC Orange ↓A, GBC Yellow ↓B, GBC Blue ←, GBC Dark Blue ←A, GBC Gray ←B, GBC Green →, GBC Dark Green →A, GBC Reverse →B, SGB 1-A, SGB 1-B, SGB 1-C, SGB 1-D, SGB 1-E, SGB 1-F, SGB 1-G, SGB 1-H, SGB 2-A, SGB 2-B, SGB 2-C, SGB 2-D, SGB 2-E, SGB 2-F, SGB 2-G, SGB 2-H, SGB 3-A, SGB 3-B, SGB 3-C, SGB 3-D, SGB 3-E, SGB 3-F, SGB 3-G, SGB 3-H, SGB 4-A, SGB 4-B, SGB 4-C, SGB 4-D, SGB 4-E, SGB 4-F, SGB 4-G, SGB 4-H]
  - mgba_frameskip: -[0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10] +[disabled, auto, auto_threshold, fixed_interval]
- label drift (old label KEPT for reviewability) (2):
  - mgba_audio_low_pass_filter: `Audio Filter` (kept) vs core's `Low Pass Filter`
  - mgba_audio_low_pass_range: `Audio Filter Level` (kept) vs core's `Filter Level`

### `mupen64plus_next`

- changed defaults (1):
  - mupen64plus-parallel-rdp-vi-bilinear: `False` -> `True`
- **token lists RECOVERED** (7) — the old snapshot carried these with an EMPTY value list, so `ParseExtracted` dropped them and the config module never showed them at all: `mupen64plus-OverscanTop`, `mupen64plus-OverscanLeft`, `mupen64plus-OverscanRight`, `mupen64plus-OverscanBottom`, `mupen64plus-parallel-rdp-overscan`, `mupen64plus-CountPerOpDenomPot`, `mupen64plus-astick-sensitivity`
- changed token lists (2):
  - mupen64plus-169screensize: +[5120x1440, 7680x2160]
  - mupen64plus-angrylion-multithread: +[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 75]
- label drift (old label KEPT for reviewability) (14):
  - mupen64plus-parallel-rdp-synchronous: `(ParaLLEl-RDP) Synchronous RDP` (kept) vs core's `Synchronous RDP`
  - mupen64plus-parallel-rdp-overscan: `(ParaLLEl-RDP) Crop overscan` (kept) vs core's `Crop overscan`
  - mupen64plus-parallel-rdp-divot-filter: `(ParaLLEl-RDP) VI Divot filter` (kept) vs core's `VI Divot filter`
  - mupen64plus-parallel-rdp-gamma-dither: `(ParaLLEl-RDP) VI Gamma dither` (kept) vs core's `VI Gamma dither`
  - mupen64plus-parallel-rdp-vi-aa: `(ParaLLEl-RDP) VI anti-aliasing` (kept) vs core's `VI anti-aliasing`
  - mupen64plus-parallel-rdp-vi-bilinear: `(ParaLLEl-RDP) VI bilinear` (kept) vs core's `VI bilinear`
  - mupen64plus-parallel-rdp-dither-filter: `(ParaLLEl-RDP) VI dither filter` (kept) vs core's `VI dither filter`
  - mupen64plus-parallel-rdp-upscaling: `(ParaLLEl-RDP) Upscaling factor (restart)` (kept) vs core's `Upscaling factor (restart)`
  - mupen64plus-parallel-rdp-super-sampled-read-back: `(ParaLLEl-RDP) SSAA framebuffer effects (restart)` (kept) vs core's `SSAA framebuffer effects (restart)`
  - mupen64plus-parallel-rdp-super-sampled-read-back-dither: `(ParaLLEl-RDP) Dither SSAA framebuffer effects (restart)` (kept) vs core's `Dither SSAA framebuffer effects (restart)`
  - mupen64plus-parallel-rdp-downscaling: `(ParaLLEl-RDP) Downsampling factor` (kept) vs core's `Downsampling factor`
  - mupen64plus-parallel-rdp-native-texture-lod: `(ParaLLEl-RDP) Native texture LOD` (kept) vs core's `Native texture LOD`
  - mupen64plus-parallel-rdp-native-tex-rect: `(ParaLLEl-RDP) Native resolution TEX_RECT` (kept) vs core's `Native resolution TEX_RECT`
  - mupen64plus-parallel-rdp-deinterlace-method: `(ParaLLEl-RDP) Deinterlacing method` (kept) vs core's `Deinterlacing method`

### `nestopia`

- **removed keys** (2): `nestopia_overscan_v`, `nestopia_overscan_h`
- changed token lists (1):
  - nestopia_show_advanced_av_settings: order only
- label drift (old label KEPT for reviewability) (1):
  - nestopia_show_advanced_av_settings: `Show Advanced Audio/Video Settings` (kept) vs core's `Show Advanced Audio Settings (Reopen menu)`

### `o2em`

- **NEW CORE** in the catalog (10 options) — closes a D7.1 gap.

### `opera`

- **NEW CORE** in the catalog (17 options) — closes a D7.1 gap.

### `parallel_n64`

- ⚠ hand-only: not written to the catalog
- policy **hand-only**: The mupen config bridge maps some DECLARED tokens to broken values — parallel-n64-screensize declares 1440x1080/2240x1680/2880x2160/5760x4320, which api/config.c's translate table does not list, so ConfigGetParamInt falls through to 0 and glide64/rice get ScreenWidth=0. Folding the declared list would offer options that are broken, not merely unavailable. Also carries MT-patched keys (countperop, send_alist_to_lle_rsp) whose hand-written notes are the only documentation they have. (extracted anyway: 94 options, outcome ok)

### `pcsx2`

- changed defaults (1):
  - pcsx2_bios: `` -> ``

### `pcsx_rearmed`

- **removed keys** (9): `pcsx_rearmed_frameskip`, `pcsx_rearmed_display_internal_fps`, `pcsx_rearmed_show_gpu_peops_settings`, `pcsx_rearmed_show_gpu_unai_settings`, `pcsx_rearmed_multitap1`, `pcsx_rearmed_multitap2`, `pcsx_rearmed_nosmccheck`, `pcsx_rearmed_gteregsunneeded`, `pcsx_rearmed_nogteflags`
- **token lists RECOVERED** (13) — the old snapshot carried these with an EMPTY value list, so `ParseExtracted` dropped them and the config module never showed them at all: `pcsx_rearmed_cd_readahead`, `pcsx_rearmed_frameskip_threshold`, `pcsx_rearmed_frameskip_interval`, `pcsx_rearmed_screen_centering_x`, `pcsx_rearmed_screen_centering_y`, `pcsx_rearmed_screen_centering_h_adj`, `pcsx_rearmed_input_sensitivity`, `pcsx_rearmed_konamigunadjustx`, `pcsx_rearmed_konamigunadjusty`, `pcsx_rearmed_gunconadjustx`, `pcsx_rearmed_gunconadjusty`, `pcsx_rearmed_gunconadjustratiox`, `pcsx_rearmed_gunconadjustratioy`
- changed token lists (1):
  - pcsx_rearmed_psxclock: +[30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100]
- label drift (old label KEPT for reviewability) (5):
  - pcsx_rearmed_neon_interlace_enable_v2: `(GPU) Show Interlaced Video` (kept) vs core's `Show Interlaced Video`
  - pcsx_rearmed_neon_enhancement_enable: `(GPU) Enhanced Resolution` (kept) vs core's `Enhanced Resolution`
  - pcsx_rearmed_neon_enhancement_no_main: `(GPU) Enhanced Resolution Speed Hack` (kept) vs core's `Enh. Res. Speed Hack`
  - pcsx_rearmed_neon_enhancement_tex_adj_v2: `(GPU) Enhanced Resolution Texture Adjustment` (kept) vs core's `Enh. Res. Texture Fixup`
  - pcsx_rearmed_spu_reverb: `Audio Reverb Effects` (kept) vs core's `Reverb Effects`

### `picodrive`

- **removed keys** (1): `picodrive_input`
- label drift (old label KEPT for reviewability) (3):
  - picodrive_region: `System Region` (kept) vs core's `Region`
  - picodrive_renderer: `Video Renderer` (kept) vs core's `Renderer`
  - picodrive_sound_rate: `Audio Sample Rate (Hz)` (kept) vs core's `Sample Rate (Hz)`

### `pokemini`

- **NEW CORE** in the catalog (13 options) — closes a D7.1 gap.

### `potator`

- **NEW CORE** in the catalog (4 options) — closes a D7.1 gap.

### `prosystem`

- **NEW CORE** in the catalog (4 options) — closes a D7.1 gap.

### `same_cdi`

- **NEW CORE** in the catalog (16 options) — closes a D7.1 gap.

### `scummvm`

- ⚠ hand-only: not written to the catalog
- policy **hand-only**: Line-by-line curated exclusions documented in ArcadeCoreOptionCatalog.cs: video_hw_acceleration MUST stay disabled (its GL mode sends RETRO_HW_FRAME_BUFFER_VALID on a software-armed room and crashed the worker, 2026-07-18), pointer_device removes mouse control outright, samplerate is a room-level 48 kHz decision, gui_* is the launcher players never see, mapper_* belongs to the site's input layer. (extracted anyway: 36 options, outcome ok)

### `snes9x`

- **removed keys** (8): `snes9x_sndchan_1`, `snes9x_sndchan_2`, `snes9x_sndchan_3`, `snes9x_sndchan_4`, `snes9x_sndchan_5`, `snes9x_sndchan_6`, `snes9x_sndchan_7`, `snes9x_sndchan_8`
- label drift (old label KEPT for reviewability) (3):
  - snes9x_overclock_cycles: `Reduce Slowdown (Hack, Unsafe)` (kept) vs core's `Reduce Slowdown (Unsafe)`
  - snes9x_reduce_sprite_flicker: `Reduce Flickering (Hack, Unsafe)` (kept) vs core's `Reduce Flickering (Unsafe)`
  - snes9x_lightgun_mode: `Light Gun Mode` (kept) vs core's `Mode`

### `stella`

- **NEW CORE** in the catalog (17 options) — closes a D7.1 gap.

### `vecx`

- **NEW CORE** in the catalog (12 options) — closes a D7.1 gap.

## 5. Deployed DLLs the catalog does not use

- `citra_libretro.dll` — core key `citra`, 26 options, outcome ok. Not the DLL `config.worker-gl.yaml` loads for this core.
- `dolphin_libretro.dll` — core key `dolphin`, 0 options, outcome no-options-before-content. Not the DLL `config.worker-gl.yaml` loads for this core.
- `flycast_libretro.dll` — core key `flycast`, 88 options, outcome ok. Not the DLL `config.worker-gl.yaml` loads for this core.
- `pcsx2_libretro.dll` — core key `pcsx2`, 64 options, outcome ok-after-retro_init. Not the DLL `config.worker-gl.yaml` loads for this core.
- `ppsspp_libretro.dll` — core key `ppsspp`, 75 options, outcome ok. Not the DLL `config.worker-gl.yaml` loads for this core.

DLLs with no `lib:` line in `config.worker-gl.yaml` (CloudRetro stock defaults, or simply unused):

- `citra_libretro.dll`
- `dolphin_libretro.dll`
- `dosbox_pure_libretro.dll`
- `flycast_libretro.dll`
- `melonds_libretro.dll`
- `pcsx2_libretro.dll`
- `ppsspp_libretro.dll`
- `snes9x_libretro.dll`

## 6. Policy

| core | disposition | reason |
|---|---|---|
| `citra` | hand-only | Same single-lever policy as melondsds: only citra_resolution_factor is exposed. citra_graphics_api stays pinned to OpenGL because touch needs the GL MouseTracker. |
| `melondsds` | hand-only | Single-lever curation by policy: only melonds_opengl_resolution is exposed. melonds_render_mode is a load-bearing pin — the software renderer cannot upscale AND the stylus/touch path is wired for the GL frame, so folding the full set would offer a toggle that breaks both. |
| `parallel_n64` | hand-only | The mupen config bridge maps some DECLARED tokens to broken values — parallel-n64-screensize declares 1440x1080/2240x1680/2880x2160/5760x4320, which api/config.c's translate table does not list, so ConfigGetParamInt falls through to 0 and glide64/rice get ScreenWidth=0. Folding the declared list would offer options that are broken, not merely unavailable. Also carries MT-patched keys (countperop, send_alist_to_lle_rsp) whose hand-written notes are the only documentation they have. |
| `scummvm` | hand-only | Line-by-line curated exclusions documented in ArcadeCoreOptionCatalog.cs: video_hw_acceleration MUST stay disabled (its GL mode sends RETRO_HW_FRAME_BUFFER_VALID on a software-armed room and crashed the worker, 2026-07-18), pointer_device removes mouse control outright, samplerate is a room-level 48 kHz decision, gui_* is the launcher players never see, mapper_* belongs to the site's input layer. |


---

## 7. Verdicts and open decisions

> ⚠ **Hand-written.** Sections 1–6 above are generated; this one is not. `scripts/extract-core-options.ps1`
> writes a **date-stamped** report (`core-options-drift-<today>.md`), so a later run creates a new file and
> leaves this analysis intact — but re-running it *today* would overwrite this section. Copy it out first.

### 7.1 The withdrawn D4 claim is confirmed withdrawn — with evidence

An earlier session read the `pcsx2_custom_libretro.dll` binary statically and concluded the deployed core had
**dropped** the `OpenGL` and `Auto` renderer tokens. That claim was withdrawn on theory (value lists are arrays
of string *pointers*; identical strings are pooled by the linker, so a contiguous ASCII run in the binary is not
a value list). §2 now settles it empirically — the core itself handed the frontend this array:

`Auto` · `OpenGL` · `D3D11` · `D3D12` · `Vulkan` · `paraLLEl-GS` · `Software (HW)` · `Software (SW)`

and the **stock** `pcsx2_libretro.dll` declares the identical eight. The custom build changed nothing about the
renderer list. Consequences:

- The `opengl` PS2 render profile is **not token-dead**. Its `pcsx2_renderer=OpenGL` is a real, declared token.
  Whether the GL path *boots, streams and zero-copies* is a separate question and still unanswered — Phase 3
  decides it by boot test, as planned. What is now off the table is retiring it because the token vanished.
- Phase 3's proposed **"Vulkan (GSdx)"** profile has a real token to use: `Vulkan`. It no longer has to be
  guessed. (Note the trap the current labels set: `Vulkan` and `paraLLEl-GS` are two different GS
  implementations, and the profile *labelled* "Vulkan" selects `paraLLEl-GS`.)
- Every renderer token the site ships today validates against the deployed DLLs: pcsx2 `paraLLEl-GS`/`OpenGL`,
  beetle `hardware_vk`/`hardware_gl`, mupen `parallel`/`gliden64` + `parallel`/`hle`, parallel_n64
  `parallel`/`gliden64`/`glide64` + `parallel`/`hle`. **Zero typos, zero retired tokens.** D5's guard has
  nothing to catch yet — which is exactly why it should exist before it does.

### 7.2 What actually drifted

Ranked by how much it mattered, not by size of the diff.

1. **20 keys the catalog offered that the deployed cores no longer declare.** libretro silently ignores an
   unknown key, so every one of these was a dropdown row that did nothing:
   `snes9x_sndchan_1`…`_8` (superseded upstream by the `snes9x_sndchan_volume_*` set the catalog *also*
   carried), `pcsx_rearmed_frameskip`, `_display_internal_fps`, `_show_gpu_peops_settings`,
   `_show_gpu_unai_settings`, `_multitap1`, `_multitap2`, `_nosmccheck`, `_gteregsunneeded`, `_nogteflags`,
   `nestopia_overscan_h`, `nestopia_overscan_v`, `picodrive_input`. All are gone from the catalog now.
2. **28 options that were in the catalog but INVISIBLE.** The old snapshot carried them with an empty
   `values` array, and `ParseExtracted` drops an enum option with no tokens as "unusable" — so they were
   listed in the JSON and never rendered. The harness recovered their real token lists: 6 on beetle_psx_hw
   (incl. `cpu_freq_scale`, `memcard_left/right_index`), 13 on pcsx_rearmed (all the screen-centering and
   light-gun adjustments), 7 on mupen64plus_next (all four `Overscan*`, `parallel-rdp-overscan`,
   `CountPerOpDenomPot`, `astick-sensitivity`), plus `reicast_coin_limit` and `dosbox_pure_midi`.
   Old catalog: 843 options, 790 usable. New: 1020 options, 1010 usable.
3. **4 defaults changed under us.** `mupen64plus-parallel-rdp-vi-bilinear` `False`→`True`,
   `genesis_plus_gx_psg_preamp` `100`→`150`, `dosbox_pure_midi` `disabled`→`frontend`,
   `mgba_frameskip` `0`→`disabled`. These feed the module's displayed baseline *and* the PUT's
   "equal to default, so drop it" rule, so a stale default silently changes what gets stored.
4. **`mgba_frameskip` changed shape entirely** — `0`…`10` became `disabled`/`auto`/`auto_threshold`/
   `fixed_interval`, and four companion keys appeared (`mgba_frameskip_threshold`, `_interval`,
   `mgba_color_correction`, `mgba_interframe_blending`). Any stored `mgba_frameskip=2` is now an invalid token.
5. **`dosbox_pure_cpu_type` lost `pentium_mmx`**; `mupen64plus-169screensize` gained `5120x1440`/`7680x2160`.
6. **Stock vs custom is almost nothing.** `flycast_custom` adds exactly one key over stock
   (`reicast_coin_limit`); `citra_custom` adds exactly one (`citra_graphics_api`); `pcsx2_custom` and
   `ppsspp_custom` declare option-for-option the same set as stock. The custom builds are patches to
   *behaviour*, not to the option surface — worth knowing, because it means a stock-sourced catalog was
   never as wrong as feared. (parallel_n64's and mupen's MT-patched keys are real, but those cores have no
   stock DLL deployed alongside to diff against.)
7. **No preset, `UltraLiveSpec` entry or hand-written catalog token broke.** `dotnet test
   src/MovieTheater.Tests` is green (344 tests) with the regenerated catalog, and no token was edited to make
   it pass. The removed keys above were all ones nothing referenced.

### 7.3 The gap this run did NOT close

**Three cores build their option table only after content is loaded**, so their catalog blocks are the OLD
committed snapshot carried over verbatim and remain *unverified against the deployed DLL*:

| core | deployed DLL | catalog block | why |
|---|---|---|---|
| `dolphin` | `dolphin_custom_libretro.dll` | 99 options, unverified | `retro_set_environment` issues exactly one call (`SET_PIXEL_FORMAT`); nothing at `retro_init` either |
| `fbneo` | `fbneo_libretro.dll` | 41 options, unverified | its option set is per-driver (dipswitches are game-specific) |
| `melondsds` | `melondsds_libretro.dll` | hand-only, so no impact | registers from content load |

The harness deliberately stops short of `retro_load_game`: that needs a real ROM, BIOS and a GPU context, and
the deployed ROM tree is off-limits to this tool. `melondsds` costs nothing (policy hand-only). `dolphin` and
`fbneo` are the residual risk — **dolphin is the largest option set in the module (99) and the one the catalog
can least vouch for.** Closing it means either driving a real room and harvesting the worker's own
`[opt] reconcile` / `DEAD keys` lines (Phase 2 already plans to mine those logs — dolphin should be first in
that harvest), or extending the harness with a load-content mode. **Not** by pretending the carried-over block
is verified.

### 7.4 Needs Eric

1. **Label drift: adopt the cores' wording, or keep ours?** The generator keeps the existing label for any key
   already in the catalog, so the diff stays reviewable — but in a few places the core is now *more correct
   than we are*. `genesis_plus_gx_psg_channel_3_volume` is labelled "PSG **Tone** Channel 3" here and "PSG
   **Noise** Channel 3" by the core, which is the factually right one; the ten `sms_fm_channel_*` labels gained
   "(YM2413)". Elsewhere ours are deliberately better (`(ParaLLEl-RDP) …` prefixes on mupen, `(GPU) …` on
   pcsx_rearmed — those group usefully and should stay). Full list in §4. Suggest: adopt genesis_plus_gx's,
   keep the rest.
2. **Do any saved per-game configs reference the 20 dead keys or the changed `mgba_frameskip` tokens?** Not
   checked here — it is a DB question (`ArcadeGameProfile.CoreOptionsJson`) and this task was read-only. It
   folds naturally into the Phase 4.4 sweep, which already has to walk every row to strip renderer keys.
   Worth widening that sweep from "renderer keys" to "any key or token the catalog no longer knows".
3. **`melonds` (the legacy core) is now catalogued (25 options) although no system maps to it** —
   `config.worker-gl.yaml` points `nds` at `melondsds`. Harmless (nothing can reach it) and useful if nds ever
   rolls back, but if it reads as clutter, add a `melonds` entry to `policy.json`.
4. **The 18 newly-mapped systems have never had a ⚙ panel.** Their option sets are small (1–42) and come
   straight from the cores, but nobody has clicked through them. `mednafen_pce` (42) and `opera` (17) are the
   two worth an eyeball before anyone tunes with them.

### 7.5 Standing effect

Re-running `pwsh -File scripts/extract-core-options.ps1 -Force` is now a **no-op when nothing drifted** — which
is what makes Phase 4.3's drift check possible: schedule that command on Ziggy (the DLLs only exist here) and
fail/alert on a non-empty §4. The per-core child process keeps a crashing core from taking the run with it, and
`policy.json` keeps regeneration from bulldozing the four hand-curated cores.
