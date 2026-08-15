"""Regenerate the favicon raster set from src/ui/public/favicon.svg.

favicon.svg is the single source of truth for the artwork. Everything else in public/
(favicon.ico, apple-touch-icon.png, the manifest icons) is derived from it by this script,
so if the artwork changes, re-run this rather than hand-editing any PNG.

    pip install playwright pillow && python -m playwright install chromium
    python scripts/gen-favicons.py

Chromium does the vector rasterization and each size is rendered NATIVELY rather than
downscaled from one large bitmap - that is what keeps the 16px .ico entry crisp. Pillow is
only used for the preview contact sheets; the .ico container is written by hand so it can
hold those per-size renders (Pillow's ICO writer resamples a single input instead).

    --preview DIR   also drop 16/32/48 renders blown up on light and dark strips, for
                    eyeballing how the icon actually reads in a tab.
"""
import argparse
import re
import struct
from pathlib import Path

from playwright.sync_api import sync_playwright

PUBLIC = Path(__file__).resolve().parent.parent / "src" / "ui" / "public"
SRC = PUBLIC / "favicon.svg"

# Opaque backdrop for the platforms that composite transparency onto black anyway
# (iOS home screen) or that want a full-bleed tile (Android/PWA).
PLATE = "#0E0F12"

# How much of the square the artwork occupies, expressed as viewBox padding.
ART = "0 0 32 32"          # full bleed  - transparent .ico
PADDED = "-3 -3 38 38"     # ~84%        - apple-touch-icon + "any" manifest icons
MASKABLE = "-8 -8 48 48"   # ~67%        - inside the maskable safe zone

ICO_SIZES = (16, 32, 48)
PLATED = [
    ("apple-touch-icon.png", PADDED, 180),
    ("icon-192.png", PADDED, 192),
    ("icon-512.png", PADDED, 512),
    ("icon-192-maskable.png", MASKABLE, 192),
    ("icon-512-maskable.png", MASKABLE, 512),
]


def variant(svg_src, view_box, plate=None):
    """Re-frame the source SVG, optionally laying an opaque plate behind the artwork."""
    s = re.sub(r'viewBox="[^"]*"', f'viewBox="{view_box}"', svg_src, count=1)
    s = re.sub(r'\swidth="32"\s+height="32"', "", s, count=1)
    if plate:
        x, y, w, h = (float(v) for v in view_box.split())
        s = s.replace(">", f'><rect x="{x}" y="{y}" width="{w}" height="{h}" fill="{plate}"/>', 1)
    return s


def render(page, svg, size, transparent):
    page.set_viewport_size({"width": size, "height": size})
    page.set_content(
        "<style>html,body{margin:0;padding:0;background:none}"
        f"svg{{display:block;width:{size}px;height:{size}px}}</style>" + svg
    )
    return page.screenshot(omit_background=transparent)


def write_ico(path, entries):
    """ICO container holding PNG-compressed entries (Vista+ and every current browser)."""
    out = struct.pack("<HHH", 0, 1, len(entries))
    offset = len(out) + 16 * len(entries)
    blobs = b""
    for size, data in entries:
        dim = size if size < 256 else 0
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    path.write_bytes(out + blobs)


def contact_sheets(page, svg, out_dir):
    from PIL import Image

    out_dir.mkdir(parents=True, exist_ok=True)
    small = {s: render(page, svg, s, True) for s in ICO_SIZES}
    for s, data in small.items():
        (out_dir / f"favicon-{s}.png").write_bytes(data)
    (out_dir / "favicon-512.png").write_bytes(render(page, svg, 512, True))

    for bg, tag in (((255, 255, 255, 255), "light"), ((24, 24, 27, 255), "dark")):
        sheet = Image.new("RGBA", (148 * len(ICO_SIZES) + 20, 168), bg)
        for i, s in enumerate(ICO_SIZES):
            img = Image.open(out_dir / f"favicon-{s}.png").convert("RGBA")
            sheet.alpha_composite(img.resize((128, 128), Image.NEAREST), (20 + 148 * i, 20))
        sheet.save(out_dir / f"sheet-{tag}.png")
    print(f"previews -> {out_dir}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview", metavar="DIR", help="also write blown-up preview sheets here")
    args = ap.parse_args()

    svg_src = SRC.read_text(encoding="utf-8")
    with sync_playwright() as p:
        browser = p.chromium.launch()
        # Pin the scheme: favicon.svg carries a prefers-color-scheme nudge, and the rasters
        # should always be the light-scheme (base) palette.
        page = browser.new_page(device_scale_factor=1, color_scheme="light")

        full = variant(svg_src, ART)
        write_ico(PUBLIC / "favicon.ico", [(s, render(page, full, s, True)) for s in ICO_SIZES])
        print(f"favicon.ico  {ICO_SIZES}")

        for name, box, size in PLATED:
            (PUBLIC / name).write_bytes(render(page, variant(svg_src, box, PLATE), size, False))
            print(f"{name}  {size}x{size}")

        if args.preview:
            contact_sheets(page, full, Path(args.preview))
        browser.close()


if __name__ == "__main__":
    main()
