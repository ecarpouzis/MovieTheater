#!/usr/bin/env python3
"""Build the /arcade console tiles from Wikimedia Commons sources.

One tile per system code: a brand-coloured gradient panel carrying the console's wordmark and a
knocked-out photo of the hardware. The tiles are committed to src/ui/src/assets/consoles, so the site
never fetches art at runtime -- this script exists so the set is REPRODUCIBLE when a system is added
or a source image is re-pointed, not so it can run in production.

Sources and the licence of every file used are read live from Commons and written to ATTRIBUTION.md,
so credit can never drift from the bytes actually fetched.

Chunked + resumable by design (see the repo's bulk-job rule): each run does a bounded number of
systems, skips ones already built, caches every download, and prints what remains. The caller drives
it to completion.

    python build-console-art.py                 # next 10 unbuilt systems
    python build-console-art.py --limit 60      # bigger batch
    python build-console-art.py --only nes,snes # named systems (implies --overwrite)
    python build-console-art.py --overwrite      # rebuild ones already on disk
    python build-console-art.py --check          # verify every manifest source resolves; fetch nothing

Requires Pillow. No other third-party dependency, on purpose.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).resolve().parent
MANIFEST = HERE / "manifest.json"
CACHE = HERE / ".cache"
OUT = HERE.parent.parent / "src" / "ui" / "src" / "assets" / "consoles"
ATTRIBUTION = OUT / "ATTRIBUTION.md"

API = "https://commons.wikimedia.org/w/api.php"
# Commons asks for a descriptive UA with contact details on API traffic.
UA = "MovieTheater-console-art/1.0 (https://github.com/ecarpouzis; ecarpouzis@gmail.com)"

# Tile geometry, at 2x. The carousel renders these at ~200x130 CSS px.
TILE_W, TILE_H = 400, 260
SOURCE_W = 1000          # width we ask Commons to render/downscale to before compositing

# Display labels, mirrored from src/ui/src/Pages/Arcade/arcadeSystems.js. Only used for the text
# fallback when a system has no logo file on Commons, so it needs to stay in step but not perfectly.
LABELS = {
    "nes": "NES", "snes": "SNES", "genesis": "Genesis", "gb": "Game Boy", "gbc": "Game Boy Color",
    "gba": "Game Boy Advance", "n64": "Nintendo 64", "gc": "GameCube", "wii": "Wii",
    "ps1": "PlayStation", "ps2": "PlayStation 2", "arcade": "Arcade", "psp": "PSP", "dc": "Dreamcast",
    "naomi": "Naomi", "atomiswave": "Atomiswave", "saturn": "Saturn", "sms": "Master System",
    "gg": "Game Gear", "sg1000": "SG-1000", "segacd": "Sega CD", "sega32x": "32X",
    "pce": "TurboGrafx-16", "ngpc": "Neo Geo Pocket", "wsc": "WonderSwan Color", "a2600": "Atari 2600",
    "a7800": "Atari 7800", "lynx": "Atari Lynx", "vb": "Virtual Boy", "fds": "Famicom Disk System",
    "neogeo": "Neo Geo", "3do": "3DO", "cdi": "CD-i", "coleco": "ColecoVision", "intv": "Intellivision",
    "vectrex": "Vectrex", "o2em": "Odyssey\u00b2", "channelf": "Channel F", "arcadia": "Arcadia 2001",
    "pokemini": "Pok\u00e9mon Mini", "supervision": "Supervision", "scummvm": "ScummVM",
    "nds": "Nintendo DS", "3ds": "Nintendo 3DS", "switch": "Switch", "ps3": "PlayStation 3",
    "ps4": "PlayStation 4", "wiiu": "Wii U", "x360": "Xbox 360", "capture": "Live", "pc": "PC",
}

# Bold faces to try for the text fallback, best first. The site's own face is woff2, which Pillow
# cannot read, so this falls back to whatever bold grotesque the build machine has.
FONT_CANDIDATES = [
    "C:/Windows/Fonts/seguisb.ttf", "C:/Windows/Fonts/segoeuib.ttf", "C:/Windows/Fonts/arialbd.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
    "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
]


# ---------------------------------------------------------------------------- Commons

# Commons throttles hard and answers 429 with no warning. Everything that touches the network goes
# through fetch(): it paces requests apart and backs off when asked to, which is the difference
# between a build that completes and one that half-fails and needs babysitting.
MIN_INTERVAL = 0.7          # seconds between requests
_last_request = 0.0


def fetch(url: str, timeout: int = 60, attempts: int = 5) -> bytes:
    global _last_request
    delay = 2.0
    for attempt in range(1, attempts + 1):
        wait = MIN_INTERVAL - (time.monotonic() - _last_request)
        if wait > 0:
            time.sleep(wait)
        req = urllib.request.Request(url, headers={"User-Agent": UA})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            if e.code not in (429, 503) or attempt == attempts:
                raise
            # Honour Retry-After when Commons sends one, else back off exponentially.
            retry_after = e.headers.get("Retry-After")
            pause = float(retry_after) if (retry_after or "").isdigit() else delay
            print(f"    ...{e.code} from Commons, waiting {pause:.0f}s (attempt {attempt}/{attempts})")
            time.sleep(pause)
            delay = min(delay * 2, 60)
        finally:
            _last_request = time.monotonic()
    raise RuntimeError("unreachable")


def api(params: dict) -> dict:
    url = API + "?" + urllib.parse.urlencode(dict(params, format="json", formatversion="2"))
    return json.loads(fetch(url, timeout=45))


def strip_html(s: str) -> str:
    return re.sub(r"\s+", " ", re.sub(r"<[^>]+>", " ", s or "")).strip()


def commons_info(title: str) -> dict:
    """Resolve a Commons file title to a raster URL at SOURCE_W plus its credit line.

    iiurlwidth is what makes SVG logos usable: Commons rasters them server-side, so the script never
    needs an SVG renderer. It also spares Commons (and us) the 5000px originals.
    """
    d = api({
        "action": "query", "titles": title, "prop": "imageinfo",
        "iiprop": "url|size|mime|extmetadata", "iiurlwidth": str(SOURCE_W),
    })
    pages = d.get("query", {}).get("pages", [])
    if not pages or pages[0].get("missing"):
        raise LookupError(f"no such Commons file: {title}")
    ii = pages[0]["imageinfo"][0]
    meta = ii.get("extmetadata", {})
    return {
        "title": title,
        "url": ii.get("thumburl") or ii["url"],
        "descurl": ii.get("descriptionurl", ""),
        "author": strip_html(meta.get("Artist", {}).get("value", "")) or "unknown",
        "license": strip_html(meta.get("LicenseShortName", {}).get("value", "")) or "see file page",
    }


def download(info: dict) -> Path:
    """Fetch to the cache. Idempotent -- a cached file is never re-fetched, which is what makes an
    interrupted run cheap to resume."""
    CACHE.mkdir(parents=True, exist_ok=True)
    safe = re.sub(r"[^A-Za-z0-9._-]", "_", info["title"])[:120]
    ext = os.path.splitext(urllib.parse.urlparse(info["url"]).path)[1] or ".png"
    dest = CACHE / f"{safe}{ext}"
    if dest.exists() and dest.stat().st_size > 0:
        return dest
    dest.write_bytes(fetch(info["url"], timeout=90))
    return dest


# ---------------------------------------------------------------------------- image work

def load_rgba(path: Path) -> Image.Image:
    img = Image.open(path)
    if img.mode == "P":
        img = img.convert("RGBA")
    return img.convert("RGBA")


def has_real_alpha(img: Image.Image) -> bool:
    """True when the source is already a cutout, so knocking out white would be pointless (and would
    risk eating a white console body)."""
    alpha = img.getchannel("A")
    lo, _hi = alpha.getextrema()
    if lo >= 250:
        return False
    # A stray soft edge is not a cutout -- require a real transparent area.
    transparent = sum(alpha.histogram()[:16])
    return transparent > (img.width * img.height) * 0.04


def knockout_white(img: Image.Image, thresh: int = 250) -> Image.Image:
    """Drop the white studio sweep behind a Commons hardware photo.

    Only the white REACHABLE FROM THE BORDER is removed, so a white console body (Dreamcast, Wii,
    PS4) survives as long as its own edge is darker than the threshold. Systems where that fails are
    pinned with "knockout": false in the manifest.
    """
    r, g, b = img.getchannel("R"), img.getchannel("G"), img.getchannel("B")
    darkest = ImageChops.darker(ImageChops.darker(r, g), b)
    white = darkest.point(lambda v: 255 if v >= thresh else 0)

    flood = white.copy()
    px = flood.load()
    w, h = flood.size
    seeds = ([(x, 0) for x in range(w)] + [(x, h - 1) for x in range(w)]
             + [(0, y) for y in range(h)] + [(w - 1, y) for y in range(h)])
    for xy in seeds:
        if px[xy] == 255:                       # still-unvisited background
            ImageDraw.floodfill(flood, xy, 128, thresh=0)
    background = flood.point(lambda v: 255 if v == 128 else 0)

    alpha = ImageChops.invert(background)
    # Erode a hair before blurring: JPEG leaves a bright fringe right at the sweep boundary, and that
    # fringe is very visible once the cutout sits on a dark gradient.
    alpha = alpha.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.GaussianBlur(1.0))
    alpha = ImageChops.multiply(alpha, img.getchannel("A"))
    out = img.copy()
    out.putalpha(alpha)
    return out


def trim(img: Image.Image) -> Image.Image:
    box = img.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox()
    return img.crop(box) if box else img


def fit(img: Image.Image, max_w: int, max_h: int) -> Image.Image:
    scale = min(max_w / img.width, max_h / img.height)
    if scale >= 1:
        return img
    return img.resize((max(1, round(img.width * scale)), max(1, round(img.height * scale))), Image.LANCZOS)


def ink_stats(img: Image.Image) -> tuple[float, float]:
    """(mean saturation, mean luminance) over the opaque pixels -- how the script decides whether a
    logo is a black wordmark that must be flipped to white, or real brand colour to leave alone."""
    small = img.resize((min(img.width, 160), min(img.height, 160)), Image.LANCZOS)
    channels = [list(small.getchannel(c).getdata()) for c in "RGBA"]
    sat_total = lum_total = 0.0
    n = 0
    for r, g, b, a in zip(*channels):
        if a < 128:
            continue
        mx, mn = max(r, g, b), min(r, g, b)
        sat_total += 0 if mx == 0 else (mx - mn) / mx
        lum_total += (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255
        n += 1
    return (sat_total / n, lum_total / n) if n else (0.0, 1.0)


def ink_coverage(img: Image.Image) -> float:
    """Share of the trimmed mark that is actually opaque ink."""
    alpha = img.getchannel("A").resize((min(img.width, 200), min(img.height, 200)), Image.LANCZOS)
    data = list(alpha.getdata())
    return sum(1 for v in data if v > 128) / len(data) if data else 0.0


def prepare_logo(img: Image.Image, ink: str | None = None) -> Image.Image:
    """Normalise a logo onto a transparent background and, if it is a dark monochrome wordmark, flip
    it to near-white so it reads on the tile's dark gradient. Coloured marks are left untouched --
    except where the manifest pins "logoInk": "white", which exists for the mixed marks the automatic
    test cannot help with (Dreamcast is a saturated orange swirl beside black type: the swirl carries
    the average over the colour threshold, while the type it sits next to stays unreadable)."""
    if not has_real_alpha(img):
        img = knockout_white(img, thresh=238)   # looser: logo scans are rarely a clean 255 white
    img = trim(img)
    if ink != "white":
        sat, lum = ink_stats(img)
        if not (sat < 0.20 and lum < 0.55):
            return img
    white = Image.new("RGBA", img.size, (245, 246, 250, 0))
    white.putalpha(img.getchannel("A"))
    return white


def gradient(base: str) -> Image.Image:
    """The tile's backdrop: a diagonal ramp derived from the system's brand colour, with a soft
    highlight where the hardware sits and a vignette to keep the edges from glowing."""
    r, g, b = int(base[1:3], 16), int(base[3:5], 16), int(base[5:7], 16)

    def mix(c, target, amt):
        return round(c + (target - c) * amt)

    top = (mix(r, 255, 0.26), mix(g, 255, 0.26), mix(b, 255, 0.26))
    bottom = (mix(r, 0, 0.62), mix(g, 0, 0.62), mix(b, 0, 0.62))

    tile = Image.new("RGB", (TILE_W, TILE_H))
    draw = ImageDraw.Draw(tile)
    # Diagonal ramp, drawn as columns over a normalised x+y so the light lands top-left.
    for y in range(TILE_H):
        for_x = y / (TILE_H - 1)
        for x in range(0, TILE_W, 4):
            t = (x / (TILE_W - 1) * 0.55 + for_x * 0.45)
            draw.rectangle(
                [x, y, x + 3, y],
                fill=(round(top[0] + (bottom[0] - top[0]) * t),
                      round(top[1] + (bottom[1] - top[1]) * t),
                      round(top[2] + (bottom[2] - top[2]) * t)),
            )

    glow = Image.new("L", (TILE_W, TILE_H), 0)
    ImageDraw.Draw(glow).ellipse([TILE_W * 0.34, TILE_H * 0.18, TILE_W * 1.06, TILE_H * 1.12], fill=64)
    glow = glow.filter(ImageFilter.GaussianBlur(46))
    tile = Image.composite(Image.new("RGB", tile.size, (255, 255, 255)), tile, glow.point(lambda v: v // 2))

    vignette = Image.new("L", (TILE_W, TILE_H), 0)
    ImageDraw.Draw(vignette).rectangle([6, 6, TILE_W - 7, TILE_H - 7], fill=255)
    vignette = vignette.filter(ImageFilter.GaussianBlur(26))
    tile = Image.composite(tile, Image.new("RGB", tile.size, (0, 0, 0)), vignette)
    return tile.convert("RGBA")


def drop_shadow(img: Image.Image, blur: int, offset: tuple[int, int], opacity: int) -> Image.Image:
    shadow = Image.new("RGBA", (img.width + blur * 4, img.height + blur * 4), (0, 0, 0, 0))
    mask = img.getchannel("A").point(lambda v: min(opacity, v))
    shadow.paste(Image.new("RGBA", img.size, (0, 0, 0, 255)), (blur * 2 + offset[0], blur * 2 + offset[1]), mask)
    return shadow.filter(ImageFilter.GaussianBlur(blur))


def load_font(size: int) -> ImageFont.FreeTypeFont:
    for path in FONT_CANDIDATES:
        if Path(path).exists():
            return ImageFont.truetype(path, size)
    return ImageFont.load_default(size)


def text_mark(label: str, max_w: int, max_h: int) -> Image.Image:
    """Fallback wordmark for the handful of systems with no logo file on Commons (the original Game
    Boy's logo, Odyssey2, Arcadia 2001) and for the capture lane, which is ours and has none."""
    size = max_h
    while size > 10:
        font = load_font(size)
        probe = Image.new("RGBA", (1, 1))
        box = ImageDraw.Draw(probe).textbbox((0, 0), label, font=font)
        if box[2] - box[0] <= max_w and box[3] - box[1] <= max_h:
            break
        size -= 2
    font = load_font(size)
    probe = Image.new("RGBA", (1, 1))
    box = ImageDraw.Draw(probe).textbbox((0, 0), label, font=font)
    mark = Image.new("RGBA", (box[2] - box[0] + 4, box[3] - box[1] + 4), (0, 0, 0, 0))
    ImageDraw.Draw(mark).text((-box[0] + 2, -box[1] + 2), label, font=font, fill=(245, 246, 250, 255))
    return mark


def build_tile(system: str, spec: dict, sources: dict) -> Image.Image:
    tile = gradient(spec["color"])
    logo_forward = spec.get("hardware") is None

    hardware = None
    if not logo_forward:
        hw = load_rgba(download(sources["hardware"]))
        if spec.get("knockout", True) and not has_real_alpha(hw):
            hw = knockout_white(hw)
        hardware = fit(trim(hw), round(TILE_W * 0.70), round(TILE_H * 0.66))

    if spec.get("logo"):
        logo = prepare_logo(load_rgba(download(sources["logo"])), spec.get("logoInk"))
        # Some Commons files raster to a near-solid rectangle -- an SVG with an opaque backing plate, or
        # a PNG scan that was never cut out. Recolouring one of those to white produces a blank white
        # box that looks deliberate on a finished tile, so refuse it instead of shipping it silently.
        # A mark that really is a solid shape (a pixel-art cabinet) opts out with "logoSolid": true.
        coverage = ink_coverage(logo)
        if coverage > 0.78 and not spec.get("logoSolid"):
            raise ValueError(
                f"{spec['logo']} rasters {coverage:.0%} solid -- it is a filled block, not a wordmark. "
                f"Pick a different Commons file, set \"logo\": null for the text fallback, or pin "
                f"\"logoSolid\": true if the mark really is solid.")
    else:
        logo = None

    if logo_forward:
        # No recognisable hardware -- the mark IS the tile, centred and large.
        mark = fit(logo, round(TILE_W * 0.72), round(TILE_H * 0.66)) if logo \
            else text_mark(LABELS.get(system, system.upper()), round(TILE_W * 0.72), 60)
        pos = ((TILE_W - mark.width) // 2, (TILE_H - mark.height) // 2)
        tile.alpha_composite(drop_shadow(mark, 7, (0, 3), 150),
                             (pos[0] - 14, pos[1] - 14))
        tile.alpha_composite(mark, pos)
        return tile

    # Hardware bottom-right, wordmark top-left.
    hx = TILE_W - hardware.width - 18
    hy = TILE_H - hardware.height - 16
    tile.alpha_composite(drop_shadow(hardware, 9, (2, 6), 130), (hx - 18, hy - 18))
    tile.alpha_composite(hardware, (hx, hy))

    # Wordmarks are wide, but a few marks are portrait (3DO, Neo Geo Pocket). Capping those at the
    # wordmark height shrinks them to a speck, so tall marks trade width for height instead.
    if logo is not None and logo.height > logo.width * 0.8:
        mark = fit(logo, round(TILE_W * 0.22), 96)
    elif logo is not None:
        mark = fit(logo, round(TILE_W * 0.46), 52)
    else:
        mark = text_mark(LABELS.get(system, system.upper()), round(TILE_W * 0.46), 40)
    tile.alpha_composite(drop_shadow(mark, 5, (0, 2), 140), (24 - 10, 22 - 10))
    tile.alpha_composite(mark, (24, 22))
    return tile


# ---------------------------------------------------------------------------- driver

def resolve_sources(spec: dict) -> dict:
    out = {}
    for slot in ("hardware", "logo"):
        if spec.get(slot):
            out[slot] = commons_info(spec[slot])
    return out


def write_attribution(manifest: dict, credits: dict) -> None:
    lines = [
        "# Console tile sources",
        "",
        "Generated by `scripts/console-art/build-console-art.py` -- do not edit by hand.",
        "",
        "Every tile in this folder is composited from files hosted on Wikimedia Commons. The licence and",
        "author below are read from Commons at build time, so they describe the exact file used. Most of",
        "the hardware photography is Evan Amos's Vanamo Online Game Museum set, released to the public",
        "domain; the CC BY-SA entries are reproduced here as the attribution that licence requires.",
        "",
        "| System | Role | Commons file | Author | Licence |",
        "| --- | --- | --- | --- | --- |",
    ]
    for system in sorted(manifest):
        for slot, info in sorted(credits.get(system, {}).items()):
            title = info["title"].removeprefix("File:")
            link = info.get("descurl") or ""
            cell = f"[{title}]({link})" if link else title
            lines.append(f"| `{system}` | {slot} | {cell} | {info['author']} | {info['license']} |")
    lines.append("")
    ATTRIBUTION.write_text("\n".join(lines), encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--limit", type=int, default=10, help="how many systems to build this run (0 = no cap)")
    ap.add_argument("--only", default="", help="comma-separated system codes; implies --overwrite")
    ap.add_argument("--overwrite", action="store_true", help="rebuild systems whose tile already exists")
    ap.add_argument("--check", action="store_true", help="verify every manifest source resolves, then stop")
    args = ap.parse_args()

    manifest = {k: v for k, v in json.loads(MANIFEST.read_text(encoding="utf-8")).items()
                if not k.startswith("$")}
    OUT.mkdir(parents=True, exist_ok=True)

    if args.check:
        bad = 0
        for system in sorted(manifest):
            for slot in ("hardware", "logo"):
                title = manifest[system].get(slot)
                if not title:
                    continue
                try:
                    commons_info(title)
                except Exception as e:  # noqa: BLE001 -- report every bad title, don't stop at the first
                    bad += 1
                    print(f"  BAD  {system:<12} {slot:<9} {title}  ({e})")
        print(f"\nchecked {len(manifest)} systems; {bad} unresolved source(s)")
        return 1 if bad else 0

    only = [s.strip() for s in args.only.split(",") if s.strip()]
    if only:
        unknown = [s for s in only if s not in manifest]
        if unknown:
            print(f"unknown system code(s): {', '.join(unknown)}", file=sys.stderr)
            return 2
        todo, overwrite = only, True
    else:
        overwrite = args.overwrite
        todo = [s for s in sorted(manifest) if overwrite or not (OUT / f"{s}.webp").exists()]

    remaining_before = len(todo)
    batch = todo if args.limit <= 0 else todo[:args.limit]

    credits, built, failed = {}, [], []
    for system in batch:
        spec = manifest[system]
        try:
            sources = resolve_sources(spec)
            tile = build_tile(system, spec, sources)
            tile.convert("RGB").save(OUT / f"{system}.webp", "WEBP", quality=86, method=6)
            credits[system] = sources
            built.append(system)
            print(f"  built {system:<12} {(OUT / f'{system}.webp').stat().st_size // 1024:>3} KB")
        except Exception as e:  # noqa: BLE001 -- one bad source must not sink the batch
            failed.append((system, repr(e)))
            print(f"  FAIL  {system:<12} {e}")

    # Attribution is rebuilt from every tile on disk, not just this batch, so a chunked run still
    # ends with a complete credit file.
    all_credits = {}
    cred_path = HERE / ".cache" / "credits.json"
    if cred_path.exists():
        all_credits = json.loads(cred_path.read_text(encoding="utf-8"))
    all_credits.update(credits)
    cred_path.parent.mkdir(parents=True, exist_ok=True)
    cred_path.write_text(json.dumps(all_credits, indent=1), encoding="utf-8")
    write_attribution({k: v for k, v in manifest.items() if (OUT / f"{k}.webp").exists()}, all_credits)

    done = len(list(OUT.glob("*.webp")))
    remaining = remaining_before - len(built)
    print(f"\nbuilt {len(built)}, failed {len(failed)}, remaining {remaining} "
          f"({done}/{len(manifest)} tiles on disk)")
    if failed:
        print("failures:")
        for system, err in failed:
            print(f"  {system}: {err}")
    if remaining > 0:
        print(f"run again to continue: python {Path(__file__).name}"
              + (f" --limit {args.limit}" if args.limit != 10 else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
