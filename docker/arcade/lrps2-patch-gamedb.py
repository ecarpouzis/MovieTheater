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
# 2. roundModes  — the ONLY known lever for the long-standing AI-pathing bug (PCSX2 issue #2990:
#    the lead car fails a corner on level 4 and softlocks it). The issue states the default Chop/Zero
#    FPU mode is what breaks it and that "Nearest" gets the car past that corner. eeRoundMode: 0 is
#    Nearest; vuRoundMode: 2 is PositiveInfinity, the VU setting the PCSX2 forum thread pairs with it.
#    ⚠ THIS IS A MITIGATION, NOT A CURE. Upstream has NO fix — the bug is open, the devs consider it
#    one of the last games to be fixed because it needs bit-accurate PS2 floating point, and the
#    reporter notes the car still fails a LATER turn even on Nearest. Do not promise a completable
#    game on the strength of this. (Round-mode enum: 0=Nearest 1=NegInf 2=PosInf 3=Chop/Zero.)
NEW = (
    b'SLUS-20250:\n'
    b'  name: "Stuntman"\n'
    b'  region: "NTSC-U"\n'
    b'  roundModes: {eeRoundMode: 0, vuRoundMode: 2}\n'
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
