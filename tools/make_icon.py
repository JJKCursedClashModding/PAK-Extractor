#!/usr/bin/env python3
"""
Generates src/app.ico — the icon used for JJKCC-Unpacker.exe.

Pure standard library: writes uncompressed 32-bit BGRA BMP frames into an ICO
container, so no image libraries are required.

    python tools/make_icon.py
"""

import os
import struct
import zlib

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src", "app.ico")
SIZES = [256, 128, 64, 48, 32, 16]
PNG_SIZES = {256, 128}   # large frames are PNG-compressed (Vista+), the rest plain BMP

# colour stops (top -> bottom) for the tile and for the diamond
TILE_TOP = (0x2B, 0x1B, 0x4E)
TILE_BOTTOM = (0x6E, 0x33, 0x9B)
DIAMOND_IN = (0x7B, 0xE7, 0xF2)
DIAMOND_OUT = (0x3F, 0x8C, 0xF0)
EDGE = (0x14, 0x0E, 0x28)


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def blend(dst, src, alpha):
    return tuple(int(round(dst[i] * (1.0 - alpha) + src[i] * alpha)) for i in range(3))


def render(size):
    """Returns BGRA bytes, top-down."""
    px = [[(0, 0, 0, 0)] * size for _ in range(size)]

    radius = size * 0.22
    cx = cy = (size - 1) / 2.0
    diamond = size * 0.30

    for y in range(size):
        for x in range(size):
            # rounded-rectangle coverage (supersampled 2x2)
            coverage = 0.0
            for sy in (0.25, 0.75):
                for sx in (0.25, 0.75):
                    fx, fy = x + sx, y + sy
                    dx = max(radius - fx, fx - (size - radius), 0.0)
                    dy = max(radius - fy, fy - (size - radius), 0.0)
                    if (dx * dx + dy * dy) <= radius * radius:
                        coverage += 0.25
            if coverage <= 0.0:
                continue

            colour = lerp(TILE_TOP, TILE_BOTTOM, y / float(max(size - 1, 1)))

            # diamond glyph
            dist = (abs(x - cx) + abs(y - cy)) / diamond
            if dist < 1.25:
                if dist <= 1.0:
                    glyph = lerp(DIAMOND_IN, DIAMOND_OUT, dist)
                    alpha = 1.0 if dist < 0.9 else max(0.0, (1.0 - dist) / 0.1)
                    colour = blend(colour, glyph, alpha)
                else:
                    colour = blend(colour, EDGE, 0.35 * (1.25 - dist) / 0.25)

            px[y][x] = (colour[2], colour[1], colour[0], int(round(coverage * 255)))
    return px


def bmp_frame(px, size):
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, size * size * 4, 0, 0, 0, 0)
    body = bytearray()
    for y in range(size - 1, -1, -1):          # bottom-up
        for x in range(size):
            body += bytes(px[y][x])
    mask_row = ((size + 31) // 32) * 4
    body += b"\x00" * (mask_row * size)        # AND mask (unused for 32-bit)
    return header + bytes(body)


def png_frame(px, size):
    raw = bytearray()
    for y in range(size):
        raw.append(0)                                  # filter: none
        for x in range(size):
            b, g, r, a = px[y][x]
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data +
                struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n" +
            chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) +
            chunk(b"IDAT", zlib.compress(bytes(raw), 9)) +
            chunk(b"IEND", b""))


def main():
    images = [(s, png_frame(render(s), s) if s in PNG_SIZES else bmp_frame(render(s), s))
              for s in SIZES]

    out = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    offset = 6 + 16 * len(images)
    for size, data in images:
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32,
                           len(data), offset)
        offset += len(data)
    for _size, data in images:
        out += data

    with open(OUT, "wb") as fh:
        fh.write(out)
    print("wrote %s (%d bytes, sizes: %s)" % (OUT, len(out), ", ".join(map(str, SIZES))))


if __name__ == "__main__":
    main()
