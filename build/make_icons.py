"""Puts the application icon in place from the designer's files in build/icons.

python build/make_icons.py

Sources (build/icons):
  nextcommander.ico      Windows: 16, 24, 32, 48, 64, 128, 256 px
  nextcommander.icns     macOS: 16 ... 1024 px (Retina sizes too)
  png/<size>.png         16, 24, 32, 48, 64, 96, 128, 256, 512, 1024 px (Linux hicolor theme and the window icon)
  nextcommander.svg      Linux: the scalable icon (hicolor/scalable)

Written:
  src/NextCommander.App/Assets/NextCommander.ico / .icns / .png (256 px window icon)
  build/windows/Assets/Square44x44Logo.png, Square150x150Logo.png, StoreLogo.png (MSIX), scaled down from 256 px
build/publish.ps1 copies png/<size>.png into the Linux packages as hicolor icons (icons/hicolor/<size>x<size>/apps).
Pure Python (zlib only).
"""
import os
import shutil
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "icons")
ASSETS = os.path.join(HERE, "..", "src", "NextCommander.App", "Assets")
MSIX = os.path.join(HERE, "windows", "Assets")


def read_png(path):
    """8-bit RGBA, non-interlaced PNG -> (width, height, rows of RGBA bytes)."""
    data = open(path, "rb").read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", path
    pos, idat, width = 8, b"", 0
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        chunk = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", chunk)
            assert (depth, color, interlace) == (8, 6, 0), f"{path}: 8-bit RGBA, not interlaced expected"
        elif kind == b"IDAT":
            idat += chunk
        pos += 12 + length
    raw = zlib.decompress(idat)
    stride = width * 4
    rows, previous = [], bytearray(stride)
    for y in range(height):
        kind = raw[y * (stride + 1)]
        line = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for x in range(stride):
            a = line[x - 4] if x >= 4 else 0
            b = previous[x]
            c = previous[x - 4] if x >= 4 else 0
            if kind == 1:
                line[x] = (line[x] + a) & 255
            elif kind == 2:
                line[x] = (line[x] + b) & 255
            elif kind == 3:
                line[x] = (line[x] + (a + b) // 2) & 255
            elif kind == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(line)
        previous = line
    return width, height, rows


def scale(width, height, rows, size):
    """Area average (premultiplied alpha) to size x size."""
    out = []
    fx, fy = width / size, height / size
    for ty in range(size):
        y0, y1 = ty * fy, (ty + 1) * fy
        line = bytearray()
        for tx in range(size):
            x0, x1 = tx * fx, (tx + 1) * fx
            acc = [0.0, 0.0, 0.0, 0.0]
            total = 0.0
            for sy in range(int(y0), min(height, int(y1 + 0.9999))):
                wy = min(y1, sy + 1) - max(y0, sy)
                row = rows[sy]
                for sx in range(int(x0), min(width, int(x1 + 0.9999))):
                    w = wy * (min(x1, sx + 1) - max(x0, sx))
                    if w <= 0:
                        continue
                    r, g, b, a = row[sx * 4:sx * 4 + 4]
                    alpha = a / 255.0
                    acc[0] += r * alpha * w
                    acc[1] += g * alpha * w
                    acc[2] += b * alpha * w
                    acc[3] += a * w
                    total += w
            a = acc[3] / total if total else 0
            k = 255.0 / a if a else 0
            line += bytes(min(255, round(v * k / total)) if total else 0 for v in acc[:3]) + bytes([min(255, round(a))])
        out.append(line)
    return out


def write_png(path, size, rows):
    raw = b"".join(b"\x00" + bytes(r) for r in rows)

    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)

    data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) \
        + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")
    open(path, "wb").write(data)


if __name__ == "__main__":
    os.makedirs(ASSETS, exist_ok=True)
    shutil.copyfile(os.path.join(SOURCE, "nextcommander.ico"), os.path.join(ASSETS, "NextCommander.ico"))
    shutil.copyfile(os.path.join(SOURCE, "nextcommander.icns"), os.path.join(ASSETS, "NextCommander.icns"))
    shutil.copyfile(os.path.join(SOURCE, "png", "256.png"), os.path.join(ASSETS, "NextCommander.png"))
    os.makedirs(MSIX, exist_ok=True)
    w, h, rows = read_png(os.path.join(SOURCE, "png", "256.png"))
    for name, size in (("Square44x44Logo.png", 44), ("Square150x150Logo.png", 150), ("StoreLogo.png", 50)):
        write_png(os.path.join(MSIX, name), size, scale(w, h, rows, size))
    print("icons written to", os.path.normpath(ASSETS), "and", os.path.normpath(MSIX))
