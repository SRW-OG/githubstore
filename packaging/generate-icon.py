"""Generate the GitHubStore icon assets from one geometric definition.

The artwork is drawn in code so every output size derives from the same
shapes; a 4x supersampled master is downsampled to get clean edges.

Run from the repository root with the bundled Python runtime:

    python packaging/generate-icon.py
"""

from __future__ import annotations

import pathlib

import numpy as np
from PIL import Image, ImageDraw

# Master artwork resolution before supersampling.
MASTER = 1024
SUPERSAMPLE = 4
CANVAS = MASTER * SUPERSAMPLE

# Rounded-tile corner radius, as a fraction of the canvas.
CORNER_RADIUS = 0.2236
# Diagonal gradient endpoints (70, 64, 219) -> (139, 92, 246).
GRADIENT_START = np.array([79, 70, 229], dtype=np.float64)
GRADIENT_END = np.array([139, 92, 246], dtype=np.float64)

ICON_SIZES = (256, 128, 96, 64, 48, 40, 32, 24, 20, 16)

ASSETS = pathlib.Path("src/GitHubStore.App/Assets")
DOCS = pathlib.Path("docs/images")


def _gradient_canvas() -> Image.Image:
    """Diagonal two-colour gradient used as the tile fill."""
    rows, columns = np.mgrid[0:CANVAS, 0:CANVAS]
    ratio = (columns + rows) / (2.0 * (CANVAS - 1))
    ratio = ratio[..., None]
    pixels = GRADIENT_START * (1.0 - ratio) + GRADIENT_END * ratio
    return Image.fromarray(pixels.astype(np.uint8), mode="RGB").convert("RGBA")


def _tile_mask() -> Image.Image:
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, CANVAS - 1, CANVAS - 1],
        radius=round(CORNER_RADIUS * CANVAS),
        fill=255,
    )
    return mask


def _glyph_layer() -> Image.Image:
    """White 'install into a tray' mark: a down arrow above an open tray."""
    layer = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    ink = (255, 255, 255, 255)
    unit = CANVAS

    # Tray: an open bracket drawn as one thick polyline with round joins.
    stroke = round(0.070 * unit)
    left, right = 0.215 * unit, 0.785 * unit
    tray_top, tray_bottom = 0.545 * unit, 0.775 * unit
    draw.line(
        [(left, tray_top), (left, tray_bottom), (right, tray_bottom), (right, tray_top)],
        fill=ink,
        width=stroke,
        joint="curve",
    )
    for x, y in ((left, tray_top), (right, tray_top)):
        draw.ellipse(
            [x - stroke / 2, y - stroke / 2, x + stroke / 2, y + stroke / 2],
            fill=ink,
        )

    # Arrow: rounded shaft merged with a triangular head pointing into the tray.
    shaft_half = 0.050 * unit
    draw.rounded_rectangle(
        [0.5 * unit - shaft_half, 0.235 * unit, 0.5 * unit + shaft_half, 0.575 * unit],
        radius=round(shaft_half),
        fill=ink,
    )
    head_half = 0.140 * unit
    draw.polygon(
        [
            (0.5 * unit - head_half, 0.495 * unit),
            (0.5 * unit + head_half, 0.495 * unit),
            (0.5 * unit, 0.700 * unit),
        ],
        fill=ink,
    )
    return layer


def build_master() -> Image.Image:
    tile = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    tile.paste(_gradient_canvas(), (0, 0), _tile_mask())
    tile.alpha_composite(_glyph_layer())
    return tile.resize((MASTER, MASTER), Image.LANCZOS)


def write_assets(master: Image.Image) -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    DOCS.mkdir(parents=True, exist_ok=True)

    master.save(ASSETS / "githubstore.ico", format="ICO", sizes=[(s, s) for s in ICON_SIZES])
    master.resize((256, 256), Image.LANCZOS).save(DOCS / "app-icon.png")

    # Tile assets referenced by Package.appxmanifest.
    for name, size in (
        ("StoreLogo.png", 50),
        ("Square44x44Logo.png", 44),
        ("Square150x150Logo.png", 150),
    ):
        master.resize((size, size), Image.LANCZOS).save(ASSETS / name)


def main() -> None:
    master = build_master()
    write_assets(master)
    print(f"Wrote {ASSETS}/githubstore.ico, {ASSETS}/StoreLogo.png, "
          f"{ASSETS}/Square44x44Logo.png, {ASSETS}/Square150x150Logo.png, {DOCS}/app-icon.png")


if __name__ == "__main__":
    main()
