#!/usr/bin/env python3
"""Generate wwwroot/favicon.ico from the same geometry as favicon.svg.

Browsers that don't take an SVG favicon (older Safari, and anything that just
requests /favicon.ico blindly) need a raster. Drawn at 4x and downsampled so the
rounded corners stay clean at 16px.

    python tools/make-favicon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

AMBER = (237, 166, 63, 255)   # --amber
INK = (19, 22, 28, 255)       # --bg
SCALE = 8                     # draw at 256 then downsample
SIZES = [16, 32, 48, 64]

OUT = Path(__file__).resolve().parent.parent / "src/ReleaseCalendar.Web/wwwroot/favicon.ico"


def draw(size: int) -> Image.Image:
    s = size * SCALE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def box(x, y, w, h):
        """Map the SVG's 32-unit viewBox onto the canvas."""
        return [x / 32 * s, y / 32 * s, (x + w) / 32 * s, (y + h) / 32 * s]

    d.rounded_rectangle(box(2, 5, 28, 25), radius=5 / 32 * s, fill=AMBER)
    d.rounded_rectangle(box(8.5, 1.5, 4, 7), radius=2 / 32 * s, fill=INK)
    d.rounded_rectangle(box(19.5, 1.5, 4, 7), radius=2 / 32 * s, fill=INK)
    d.rectangle(box(2, 5, 28, 7.5), fill=INK)
    d.rounded_rectangle(box(12, 17, 8, 8), radius=2 / 32 * s, fill=INK)

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    frames = [draw(n) for n in SIZES]
    frames[0].save(OUT, format="ICO", sizes=[(n, n) for n in SIZES], append_images=frames[1:])
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes, sizes {SIZES})")


if __name__ == "__main__":
    main()
