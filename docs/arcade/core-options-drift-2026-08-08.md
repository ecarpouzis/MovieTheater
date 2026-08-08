# Arcade core-option drift report — 2026-08-08

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
| `flycast_custom_libretro.dll` | `flycast` | yes | **ok** | `SET_CORE_OPTIONS_V2_INTL` | 90 | Flycast (f09d1f2) |
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
| `pcsx2_custom_libretro.dll` | `pcsx2` | yes | **ok-after-retro_init** | `SET_CORE_OPTIONS_V2_INTL` | 68 | LRPS2 (v2.0.0-34a5cc1) |
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

- only on the deployed build (2): `reicast_coin_limit`, `reicast_mt_sort_key`
- only on the other build (0): —
- different token lists (0): —
- different defaults (0): 

### `pcsx2` — catalog uses `pcsx2_custom_libretro.dll`

vs `pcsx2_libretro.dll` (64 options, outcome ok-after-retro_init):

- only on the deployed build (4): `pcsx2_pgs_field_fullres`, `pcsx2_softfloat`, `pcsx2_softfloat_scope`, `pcsx2_softfloat_vu0micro`
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
| `amiarcadia` | fold | 3 | 3 | 0 | 0 | 0 | 0 |
| `beetle_psx_hw` | fold | 81 | 81 | 0 | 0 | 0 | 0 |
| `citra` | hand-only | — | — | 0 | 0 | 0 | 0 |
| `dolphin` | fold | 99 | 0 | 0 | 0 | 0 | 0 |
| `dosbox_pure` | fold | 48 | 48 | 0 | 0 | 0 | 0 |
| `fbneo` | fold | 41 | 0 | 0 | 0 | 0 | 0 |
| `flycast` | fold | 90 | 90 | 0 | 0 | 0 | 0 |
| `freechaf` | fold | 1 | 1 | 0 | 0 | 0 | 0 |
| `freeintv` | fold | 2 | 2 | 0 | 0 | 0 | 0 |
| `gearcoleco` | fold | 7 | 7 | 0 | 0 | 0 | 0 |
| `genesis_plus_gx` | fold | 62 | 62 | 0 | 0 | 0 | 0 |
| `kronos` | fold | 17 | 17 | 0 | 0 | 0 | 0 |
| `mednafen_lynx` | fold | 3 | 3 | 0 | 0 | 0 | 0 |
| `mednafen_ngp` | fold | 1 | 1 | 0 | 0 | 0 | 0 |
| `mednafen_pce` | fold | 42 | 42 | 0 | 0 | 0 | 0 |
| `mednafen_vb` | fold | 7 | 7 | 0 | 0 | 0 | 0 |
| `mednafen_wswan` | fold | 9 | 9 | 0 | 0 | 0 | 0 |
| `melonds` | fold | 25 | 25 | 0 | 0 | 0 | 0 |
| `melondsds` | hand-only | — | — | 0 | 0 | 0 | 0 |
| `mgba` | fold | 17 | 17 | 0 | 0 | 0 | 0 |
| `mupen64plus_next` | fold | 86 | 86 | 0 | 0 | 0 | 0 |
| `nestopia` | fold | 34 | 34 | 0 | 0 | 0 | 0 |
| `o2em` | fold | 10 | 10 | 0 | 0 | 0 | 0 |
| `opera` | fold | 17 | 17 | 0 | 0 | 0 | 0 |
| `parallel_n64` | hand-only | — | — | 0 | 0 | 0 | 0 |
| `pcsx2` | fold | 67 | 68 | 1 | 0 | 0 | 0 |
| `pcsx_rearmed` | fold | 53 | 53 | 0 | 0 | 0 | 0 |
| `picodrive` | fold | 21 | 21 | 0 | 0 | 0 | 0 |
| `pokemini` | fold | 13 | 13 | 0 | 0 | 0 | 0 |
| `potator` | fold | 4 | 4 | 0 | 0 | 0 | 0 |
| `ppsspp` | fold | 75 | 75 | 0 | 0 | 0 | 0 |
| `prosystem` | fold | 4 | 4 | 0 | 0 | 0 | 0 |
| `same_cdi` | fold | 16 | 16 | 0 | 0 | 0 | 0 |
| `scummvm` | hand-only | — | — | 0 | 0 | 0 | 0 |
| `snes9x` | fold | 40 | 40 | 0 | 0 | 0 | 0 |
| `stella` | fold | 17 | 17 | 0 | 0 | 0 | 0 |
| `vecx` | fold | 12 | 12 | 0 | 0 | 0 | 0 |

### `citra`

- ⚠ hand-only: not written to the catalog
- policy **hand-only**: Same single-lever policy as melondsds: only citra_resolution_factor is exposed. citra_graphics_api stays pinned to OpenGL because touch needs the GL MouseTracker. (extracted anyway: 27 options, outcome ok)

### `dolphin`

- ⚠ extraction no-options-before-content — OLD catalog block carried over verbatim

### `fbneo`

- ⚠ extraction no-options-before-content — OLD catalog block carried over verbatim

### `melondsds`

- ⚠ hand-only: not written to the catalog
- policy **hand-only**: Single-lever curation by policy: only melonds_opengl_resolution is exposed. melonds_render_mode is a load-bearing pin — the software renderer cannot upscale AND the stylus/touch path is wired for the GL frame, so folding the full set would offer a toggle that breaks both. (extracted anyway: 0 options, outcome no-options-before-content)

### `parallel_n64`

- ⚠ hand-only: not written to the catalog
- policy **hand-only**: The mupen config bridge maps some DECLARED tokens to broken values — parallel-n64-screensize declares 1440x1080/2240x1680/2880x2160/5760x4320, which api/config.c's translate table does not list, so ConfigGetParamInt falls through to 0 and glide64/rice get ScreenWidth=0. Folding the declared list would offer options that are broken, not merely unavailable. Also carries MT-patched keys (countperop, send_alist_to_lle_rsp) whose hand-written notes are the only documentation they have. (extracted anyway: 94 options, outcome ok)

### `pcsx2`

- added keys (1): `pcsx2_pgs_field_fullres`

### `scummvm`

- ⚠ hand-only: not written to the catalog
- policy **hand-only**: Line-by-line curated exclusions documented in ArcadeCoreOptionCatalog.cs: video_hw_acceleration MUST stay disabled (its GL mode sends RETRO_HW_FRAME_BUFFER_VALID on a software-armed room and crashed the worker, 2026-07-18), pointer_device removes mouse control outright, samplerate is a room-level 48 kHz decision, gui_* is the launcher players never see, mapper_* belongs to the site's input layer. (extracted anyway: 36 options, outcome ok)

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

