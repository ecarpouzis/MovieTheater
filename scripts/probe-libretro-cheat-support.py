#!/usr/bin/env python3
"""Does a libretro core actually IMPLEMENT retro_cheat_set, or is it an empty stub?

    python scripts/probe-libretro-cheat-support.py D:/ArcadeStorage/worker-gl/assets/cores/*.dll

Why this exists (docs/arcade-cheats.md): retro_cheat_set is a MANDATORY part of the libretro API, so
every core exports the symbol. Plenty of cores implement it as an empty function because they read their
own cheat format from disk instead (pcsx2 -> .pnach) or carry an internal cheat engine (flycast, fbneo).
A stub accepts the code and silently discards it, and nothing observable at runtime distinguishes that
from a code that simply had no visible effect. Offering cheats on such a system is a lie the UI cannot
detect, so `ArcadeCheatCatalog.SupportsCheatCodes` is an allowlist -- and THIS is how you earn a place on it.

The test: a stub's FIRST instruction is `ret`.

  Do NOT use "does the body contain any opcode other than ret" -- disassembling past the function's
  single `ret` runs into the compiler's inter-function alignment padding (`data16 nopl ...`), which
  belongs to no function at all, and that test then calls every stub REAL. First instruction only.

Requires objdump (MSYS2 / binutils) on PATH. Windows PE DLLs; works on ELF .so too.
"""
import os
import re
import subprocess
import sys


def _objdump(*args) -> str:
    return subprocess.run(["objdump", *args], capture_output=True, text=True, errors="replace").stdout


def _exports(dll: str):
    """(image_base, {export name: RVA}) from the PE export tables."""
    out = _objdump("-p", dll)
    m = re.search(r"ImageBase\s+([0-9a-fA-F]+)", out)
    base = int(m.group(1), 16) if m else 0

    ordinal_to_rva = {}
    section = out.split("Export Address Table -- Ordinal Base")
    for line in (section[1].splitlines() if len(section) > 1 else []):
        m = re.match(r"\s*\[\s*\d+\]\s*\+base\[\s*(\d+)\]\s+([0-9a-fA-F]+)\s+Export RVA", line)
        if m:
            ordinal_to_rva[int(m.group(1))] = int(m.group(2), 16)

    name_to_rva = {}
    section = out.split("[Ordinal/Name Pointer] Table")
    for line in (section[1].splitlines() if len(section) > 1 else []):
        m = re.match(r"\s*\[\s*\d+\]\s*\+base\[\s*(\d+)\]\s+\S+\s+(\S+)", line)
        if m and int(m.group(1)) in ordinal_to_rva:
            name_to_rva[m.group(2)] = ordinal_to_rva[int(m.group(1))]
    return base, name_to_rva


def _first_instruction(dll: str, addr: int) -> str | None:
    dis = _objdump("-d", f"--start-address={addr}", f"--stop-address={addr + 24}", dll)
    for line in dis.splitlines():
        parts = line.split("\t")
        if len(parts) >= 3 and ":" in parts[0]:
            return parts[2].strip().split()[0]
    return None


def probe(dll: str) -> dict[str, tuple[str, str]]:
    base, names = _exports(dll)
    result = {}
    for fn in ("retro_cheat_reset", "retro_cheat_set"):
        rva = names.get(fn)
        if not rva:
            result[fn] = ("MISSING", "-")
            continue
        first = _first_instruction(dll, base + rva)
        result[fn] = ("STUB" if first in ("ret", "retq") else "REAL", first or "?")
    return result


def main(paths: list[str]) -> int:
    if not paths:
        print(__doc__)
        return 2
    for dll in paths:
        name = os.path.basename(dll).replace("_libretro.dll", "").replace("_libretro.so", "")
        try:
            r = probe(dll)
        except Exception as e:  # a non-library file, a stripped binary, ...
            print(f"{name:24} ERROR {e}")
            continue
        s, sf = r["retro_cheat_set"]
        c, cf = r["retro_cheat_reset"]
        print(f"{name:24} cheat_set={s:7}({sf:6})  cheat_reset={c:7}({cf})")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
