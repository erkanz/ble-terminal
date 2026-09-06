#!/usr/bin/env python3
import math
import struct
import sys
from pathlib import Path

SIZE = 64
BG = (0, 0, 0, 0)
CYAN = (62, 224, 255, 255)
BLUE = (82, 128, 255, 255)
PURPLE = (182, 88, 255, 255)
WHITE = (222, 247, 255, 255)
px = [[BG for _ in range(SIZE)] for __ in range(SIZE)]


def put(x, y, color):
    if 0 <= x < SIZE and 0 <= y < SIZE:
        old = px[y][x]
        if color[3] >= old[3]:
            px[y][x] = color


def disc(cx, cy, radius, color):
    rr = radius * radius
    for y in range(int(cy - radius), int(cy + radius) + 1):
        for x in range(int(cx - radius), int(cx + radius) + 1):
            if (x - cx) ** 2 + (y - cy) ** 2 <= rr:
                put(x, y, color)


def line(x0, y0, x1, y1, width, color):
    steps = max(abs(x1 - x0), abs(y1 - y0), 1) * 3
    for i in range(steps + 1):
        t = i / steps
        disc(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, width / 2, color)


def arc(cx, cy, radius, a0, a1, width, color):
    steps = max(16, int(abs(a1 - a0) * radius / 3))
    for i in range(steps + 1):
        a = a0 + (a1 - a0) * i / steps
        disc(cx + math.cos(a) * radius, cy + math.sin(a) * radius, width / 2, color)


def polygon(points, width, color):
    for a, b in zip(points, points[1:] + points[:1]):
        line(*a, *b, width, color)


# Wireless arcs.
for radius, color in ((9, CYAN), (14, BLUE), (19, PURPLE)):
    arc(32, 15, radius, math.radians(205), math.radians(335), 1.6, color)

# Bluetooth rune.
line(32, 8, 32, 26, 2.2, CYAN)
line(32, 8, 38, 13, 2.2, BLUE)
line(38, 13, 32, 17, 2.2, BLUE)
line(32, 17, 38, 22, 2.2, PURPLE)
line(38, 22, 32, 26, 2.2, PURPLE)
line(27, 12, 36, 20, 1.8, CYAN)
line(27, 22, 36, 14, 1.8, CYAN)

# DB9/serial connector.
outline = [(14, 33), (18, 29), (46, 29), (50, 33), (47, 47), (17, 47)]
polygon(outline, 2.2, CYAN)
for cx in (10, 54):
    disc(cx, 38, 3.2, BLUE if cx < 32 else PURPLE)
for x in (22, 27, 32, 37, 42):
    disc(x, 35, 1.5, WHITE)
for x in (24.5, 29.5, 34.5, 39.5):
    disc(x, 41, 1.5, BLUE if x < 32 else PURPLE)

# Terminal prompt.
line(20, 53, 25, 56, 2, CYAN)
line(25, 56, 20, 59, 2, CYAN)
line(29, 59, 42, 59, 2, PURPLE)

# ICO with one 64x64 32-bpp DIB. The DIB height includes XOR + AND masks.
xor = bytearray()
for y in range(SIZE - 1, -1, -1):
    for r, g, b, a in px[y]:
        xor += bytes((b, g, r, a))

mask_row_bytes = ((SIZE + 31) // 32) * 4
and_mask = bytearray(mask_row_bytes * SIZE)
for out_row, y in enumerate(range(SIZE - 1, -1, -1)):
    for x in range(SIZE):
        if px[y][x][3] == 0:
            index = out_row * mask_row_bytes + x // 8
            and_mask[index] |= 0x80 >> (x % 8)

bitmap_info = struct.pack(
    '<IIIHHIIIIII',
    40,
    SIZE,
    SIZE * 2,
    1,
    32,
    0,
    len(xor),
    0,
    0,
    0,
    0,
)
image = bitmap_info + xor + and_mask
header = struct.pack('<HHH', 0, 1, 1)
entry = struct.pack('<BBBBHHII', SIZE, SIZE, 0, 0, 1, 32, len(image), 6 + 16)

output_path = Path(sys.argv[1] if len(sys.argv) > 1 else 'Assets/BLESerialTerminal.ico')
output_path.parent.mkdir(parents=True, exist_ok=True)
output_path.write_bytes(header + entry + image)
print(f'Generated {output_path} ({output_path.stat().st_size} bytes)')
