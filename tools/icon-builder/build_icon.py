#!/usr/bin/env python3
"""Technical resize/repackage of the generated source artwork into shipping icon assets.

This mirrors Build-Icon.ps1 for environments without PowerShell/System.Drawing. It does
not alter the artwork: it only produces a 256px transparent PNG and a multi-size,
PNG-compressed ICO (16..256) that preserves alpha. Requires Pillow.
"""
import io
import os
import struct
import sys

from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def resized_png_bytes(image: Image.Image, size: int) -> bytes:
    frame = image.resize((size, size), Image.LANCZOS)
    buffer = io.BytesIO()
    frame.save(buffer, format="PNG")
    return buffer.getvalue()


def main() -> int:
    source = os.path.join(ROOT, "src/XTranslate.App/Assets/xtranslate-source.png")
    png_out = os.path.join(ROOT, "src/XTranslate.App/Assets/xtranslate.png")
    ico_out = os.path.join(ROOT, "src/XTranslate.App/Assets/xtranslate.ico")

    image = Image.open(source).convert("RGBA")

    with open(png_out, "wb") as handle:
        handle.write(resized_png_bytes(image, 256))

    frames = [(size, resized_png_bytes(image, size)) for size in SIZES]
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries = b""
    payload = b""
    for size, data in frames:
        dimension = 0 if size == 256 else size
        entries += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        payload += data
    with open(ico_out, "wb") as handle:
        handle.write(header + entries + payload)

    print(f"Wrote {png_out} and {ico_out}; ICO frames: {', '.join(map(str, SIZES))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
