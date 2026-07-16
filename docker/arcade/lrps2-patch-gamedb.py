#!/usr/bin/env python3
"""Patch LRPS2's EMBEDDED GameDB for a single game, in place, byte-for-byte.

Why this exists
---------------
LRPS2's per-game fix database (12,806 games) is compiled INTO pcsx2_libretro.dll as one contiguous
plaintext YAML blob. There is no supported way to override it on this port:

  * `pcsx2_use_external_gameindex` ("Load the game-compatibility database from
    <system>/resources/GameIndex.yaml if present") is DEAD CODE. Re-tested 2026-07-14 with the file
    staged at the documented path: the core still logged "[GameDB] 12806 games on record" and never
    emitted its own "External GameIndex.yaml not found at '%s'" line — i.e. the code path never runs
    at all, so it is not a path or a value-token problem.
  * The rounding/clamping levers (eeRoundMode, vuRoundMode, eeClampMode, vuClampMode) exist in the
    parser but are NOT exposed as core options — only the GameDB can set them.
  * `pcsx2_enable_hw_hacks` would let us set the GS fixes by hand, but it DISABLES the whole GameDB
    auto-fix system (the core says so verbatim), which is a net loss.

So: patch the blob. This is a DATA edit, not a code edit.

The hard constraint
-------------------
The blob's length is fixed in the binary, so the replacement entry must be EXACTLY the same number of
bytes as the original. That is why the new entry uses YAML *flow* style ({a: 1, b: 2}) and drops the
comments and `compat:` (a display-only compatibility rating) — it buys the bytes for the real fixes
without touching a single byte outside the entry. Nothing shifts; every other game is untouched.

Output goes to a COPY (pcsx2_custom_libretro.dll) and config.worker-gl.yaml pins `lib:` to it, so
`libretro.cores.repo.sync` (which pulls nightly cores by name) can never silently overwrite the patch
— the same guard the custom Dolphin core uses. Re-run this after a core update.

Usage:  python lrps2-patch-gamedb.py <cores-dir>            # default: D:\\ArcadeStorage\\worker-gl\\assets\\cores
"""
import shutil
import sys
from pathlib import Path

SRC_NAME = "pcsx2_libretro.dll"
DST_NAME = "pcsx2_custom_libretro.dll"

# ── Stuntman (SLUS-20250) ────────────────────────────────────────────────────────────────────────
# ORIGINAL (189 bytes, verbatim from the stock DLL):
OLD = (
    b'SLUS-20250:\n'
    b'  name: "Stuntman"\n'
    b'  region: "NTSC-U"\n'
    b'  compat: 5\n'
    b'  gameFixes:\n'
    b'    - BlitInternalFPSHack # Fixes internal FPS detection.\n'
    b'  gsHWFixes:\n'
    b'    cpuSpriteRenderBW: 4 # Fixes textures.\n'
)

# NEW — two independent changes, both keyed to this serial alone:
#
# 1. halfPixelOffset: 4  — straight from UPSTREAM PCSX2 master's own entry for this game ("Fixes
#    misaligned post-processing"). Our embedded DB predates it. It matters more for us than for a
#    desktop user because we render at 2x native, which is exactly when half-pixel misalignment
#    becomes visible. (Upstream also adds `drawBuffering: 1`, but that key does not exist in this
#    fork's parser — 0 occurrences in the DLL — so it would be silently ignored. Omitted.)
#
# 2. clampModes: {eeClampMode: 3}  — the EE-FPU ACCURACY lever for the long-standing AI-pathing bug
#    (PCSX2 issue #2990: the lead car fails a corner on level 4 and softlocks it). Full write-up of
#    the whole investigation below — read it before touching this again.
#
#    ── The bug ──
#    Stuntman's AI follows a lead car whose path is computed in the PS2's NON-IEEE-754 floating point.
#    PCSX2's default FP behavior drifts enough that the lead car fails a corner and softlocks level 4.
#    Issue #2990 is OPEN/UNRESOLVED. The devs consider it one of the last games to fix because it needs
#    bit-accurate PS2 FP. The ONLY complete fix upstream has is PR #12001 (soft-float — a software
#    reimplementation of the PS2 FPU). A community fix confirms 100% completion via DESKTOP PCSX2: enable
#    EE "Software Float" (Add/Sub, Mul/Div, Sqrt) + DISABLE the EE recompiler, AND VU0 "Software Float"
#    (Add/Sub, Mul/Div, Sqrt) + DISABLE the VU0 micro-recompiler (VU1 left on the recompiler). That is
#    INTERPRETER-ONLY by nature (soft-float lives on the interpreter path), so it is far too slow for our
#    real-time shared stream. And it is flatly UNREACHABLE on our core: a full DLL string scan of
#    v2.0.0-b03969a found ZERO `SoftFloat`/`Software Float`/`Interpreter` strings and NO recompiler-toggle
#    core options (only pcsx2_cpu_sprite_level/size, pcsx2_ee_cycle_rate/skip) — the feature is not
#    compiled in and there is no interpreter path to fall back to. Getting it = a from-source custom LRPS2
#    build with the PR (days) that would then run SLOWER. Soft-float is a hard dead end for this arcade;
#    a fully-completable Stuntman is not achievable on this stack today. (2026-07-15.)
#
#    ── What we tried, in order (all measured, same box/2x upscale) ──
#    a) roundModes {eeRoundMode: 0, vuRoundMode: 2}  →  RETRACTED. vuRoundMode: 2 (PositiveInfinity)
#       knocks the VU recompiler off its fast path on the per-frame geometry and TANKED gameplay ~3x
#       (stock 60fps vs patched 16-20fps; not GPU, not contention — the GPU idled/downclocked BECAUSE
#       the EE thread was the bottleneck; a manual `nvidia-smi -lgc 2100,3000` changed nothing).
#    b) roundModes {eeRoundMode: 0} alone  →  RETRACTED. Restored a 60fps BASELINE (proven: 3.5 min
#       flat 60fps), but gameplay still dropped to ~16fps while menus/cinematics stayed 60. Round modes
#       do not even fix the AI per #2990 (Nearest only clears the FIRST corner, fails the next), so we
#       are paying an FP-accuracy tax for nothing. Dropped.
#    c) clampModes {eeClampMode: 3}  ←  CURRENT. Clamp mode is the RECOMPILER's FP-accuracy mechanism
#       (the same family as soft-float, but it stays in the fast JIT). Our own embedded GameDB uses
#       eeClampMode: 3 for exactly this bug class on other titles: "Corrects crazy car AI and prevents
#       crash" / "Reduces FPU calculation errors" / "Fixes Abnormal AI behavior". It is the strongest
#       FP accuracy that can actually run at speed. ⚠ It MAY still not complete level 4 (only soft-float
#       fully does), and Full clamp has its OWN per-op cost — so VERIFY both the car AI AND the in-game
#       framerate after this. If clamp also drags gameplay, accuracy and speed cannot coexist via FP
#       settings and the real combo is eeClampMode: 3 (AI) + native res via game-overrides.json (speed),
#       since the cpuSpriteRenderBW readback cost is SEPARATE from FP.
#       RESULT (2026-07-15, live): "far more playable" — mostly 60fps/audio 48000, meanTick ~4ms→~10ms
#       (clamp overhead fits the 16.6ms budget). Remaining issue = AUDIO CUTOUTS at the "exact same
#       point" — two kinds, log-correlated: (i) shader-compile stalls (self-heal as gl_programs caches);
#       (ii) DETERMINISTIC heavy-scene dips (no shader activity; clamp:3 FP + 2x readback exceed budget →
#       audio starves; PS2 audio is a speed symptom). So the native-res complement was STAGED:
#       `pcsx2_upscale_multiplier: "1x Native (PS2)"` for "Stuntman (USA)" in game-overrides.json on both
#       ConfDirs (hot-reload, revert to `{}`). It restores headroom for (ii); it will NOT fix an
#       FP-burst residue (that needs clamp:2 or the unavailable soft-float). Correct-AI test still pending.
#    (Clamp enum: 0=None 1=Normal 2=Extra+PreserveSign 3=Full. Round enum: 0=Nearest 1=NegInf 2=PosInf
#     3=Chop/Zero. Neither round nor clamp is a core option on this port — GameDB is the only path,
#     which is the whole reason this byte-patch exists; upscale IS a core option, hence game-overrides.json.)
#
#    Verify in the worker log: the core should print a "(GameDB) Changing EE ... clamp mode [mode=3]"
#    style line at boot (the round-mode variant logged "Changing EE/FPU roundmode to 0 [Nearest]").
NEW = (
    b'SLUS-20250:\n'
    b'  name: "Stuntman"\n'
    b'  region: "NTSC-U"\n'
    b'  clampModes: {eeClampMode: 2}\n'
    b'  gameFixes: [BlitInternalFPSHack]\n'
    b'  gsHWFixes: {cpuSpriteRenderBW: 4, halfPixelOffset: 4}\n'
)


def main() -> int:
    cores = Path(sys.argv[1] if len(sys.argv) > 1 else r"D:\ArcadeStorage\worker-gl\assets\cores")
    src, dst = cores / SRC_NAME, cores / DST_NAME
    if not src.is_file():
        print(f"ERROR: {src} not found", file=sys.stderr)
        return 1

    data = src.read_bytes()

    # Pad the replacement to the original's exact length. YAML ignores trailing blank space, and this
    # keeps every byte after the entry exactly where the binary expects it.
    if len(NEW) > len(OLD):
        print(f"ERROR: new entry is {len(NEW)}B, will not fit in {len(OLD)}B", file=sys.stderr)
        return 1
    new = NEW + b" " * (len(OLD) - len(NEW) - 1) + b"\n" if len(NEW) < len(OLD) else NEW

    n = data.count(OLD)
    if n != 1:
        print(f"ERROR: expected exactly 1 match for the stock Stuntman entry, found {n}.", file=sys.stderr)
        print("       The core was probably updated and its GameDB entry changed — re-derive OLD", file=sys.stderr)
        print("       from the new DLL before trusting this patch.", file=sys.stderr)
        return 1

    patched = data.replace(OLD, new, 1)
    assert len(patched) == len(data), "length changed — refusing to write"

    shutil.copyfile(src, dst)          # start from a pristine copy, never patch the stock DLL
    dst.write_bytes(patched)

    diffs = sum(1 for a, b in zip(data, patched) if a != b)
    print(f"patched {dst}")
    print(f"  entry: {len(OLD)}B -> {len(new)}B (padded), file size unchanged ({len(patched)} B)")
    print(f"  bytes differing: {diffs} (all inside the SLUS-20250 entry)")
    print("\nnew entry:")
    print(new.decode().rstrip())
    print("\nNow set `lib: pcsx2_custom_libretro` in config.worker-gl.yaml and restart the worker.")
    print("Verify in the worker log: 'Enabled GS Hardware Fix: halfPixelOffset' must appear.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
