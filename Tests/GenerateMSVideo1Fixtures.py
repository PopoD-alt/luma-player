"""Independent, deliberately simple Video 1 packet/AVI fixture author.

No third-party codec, encoder, or decoder is called. The semantic tile planes
are retained before packet serialization to produce exact top-down RGBA oracles.
Format description: https://multimedia.cx/video1.txt ; RGB555 masks: Microsoft
https://learn.microsoft.com/windows/win32/directshow/working-with-16-bit-rgb
"""
from pathlib import Path
import json
import struct

ROOT = Path(__file__).resolve().parent / "Fixtures"
TARGET = ROOT / "msvideo1"
TARGET.mkdir(parents=True, exist_ok=True)
PALETTE = [(0, 0, 0), (255, 0, 0), (0, 255, 0), (0, 0, 255),
           (255, 255, 255), (255, 255, 0), (255, 0, 255), (0, 255, 255),
           (132, 66, 33), (33, 132, 66), (66, 33, 132), (82, 165, 247),
           (247, 82, 165), (165, 247, 82), (99, 99, 99), (198, 198, 198)]
RGB555 = [((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3) for r, g, b in PALETTE]


def word(value):
    return struct.pack("<H", value)


def color(value, depth):
    return bytes([value]) if depth == 8 else word(value)


def tile(depth, mode, phase):
    values = list(range(16)) if depth == 8 else RGB555
    choices = [values[(phase + i) % len(values)] for i in range(8)]
    if mode == 0:
        value = choices[0]
        # 16-bit solid words whose high byte is 84..87 are skip opcodes.
        # Represent such a color with a two-color block instead.
        if depth == 16 and 0x84 <= ((value | 0x8000) >> 8) <= 0x87:
            return word(0) + color(value, depth) * 2, [value] * 16
        opcode = bytes([value, 0x80]) if depth == 8 else word(value | 0x8000)
        return opcode, [value] * 16
    if mode == 1:
        # Bottom-to-top pixel choice rows; last flag is zero, as required by
        # the two-color opcode namespace. The oracle uses the declared rows.
        rows = ["1001", "0110", "1100", "0010"]
        colors = choices[:2]
        bits = "".join(rows)
        flags = sum((symbol == "1") << i for i, symbol in enumerate(bits))
        encoded = word(flags) + b"".join(color(v, depth) for v in colors)
        return encoded, [colors[0 if symbol == "1" else 1] for symbol in bits]
    # Each 2x2 quadrant gets its own pair; the indexed namespace forces bits
    # 12 and 15 to one. The RGB555 namespace forces bit 15 to zero instead.
    rows = ["1010", "0101", "1100", "1001" if depth == 8 else "1000"]
    bits = "".join(rows)
    flags = sum((symbol == "1") << i for i, symbol in enumerate(bits))
    colors = choices[:]
    if depth == 16:
        colors[0] |= 0x8000
    encoded = word(flags) + b"".join(color(v, depth) for v in colors)
    plane = []
    for row in range(4):
        for column in range(4):
            pair = ((row // 2) * 2 + column // 2) * 2
            plane.append(choices[pair + (0 if rows[row][column] == "1" else 1)])
    return encoded, plane


def skip(count):
    output = b""
    while count:
        piece = min(count, 1023)
        output += bytes([piece & 255, 0x84 + (piece >> 8)])
        count -= piece
    return output


def rgba_from_tiles(tiles, width, height, depth):
    plane = [None] * (width * height)
    for number, pixels in enumerate(tiles):
        left, bottom = (number % (width // 4)) * 4, (number // (width // 4)) * 4
        for row in range(4):
            for column in range(4):
                value = pixels[row * 4 + column]
                if depth == 8:
                    rgb = PALETTE[value]
                else:
                    components = [(value >> shift) & 31 for shift in (10, 5, 0)]
                    rgb = tuple((component << 3) | (component >> 2) for component in components)
                plane[(height - 1 - bottom - row) * width + left + column] = bytes((*rgb, 255))
    return b"".join(plane)


def chunk(tag, body):
    return tag + struct.pack("<I", len(body)) + body + b"\0" * (len(body) & 1)


def avi(depth, packets, width, height):
    count, fps = len(packets), 2
    largest = max(map(len, packets))
    avih = struct.pack("<14I", 500000, largest * fps, 0, 0x10, count, 0, 1,
                       largest, width, height, 0, 0, 0, 0)
    strh = struct.pack("<4s4sIHH8Ihhhh", b"vids", b"MSVC", 0, 0, 0, 0,
                      1, fps, 0, count, largest, 0xffffffff, 0, 0, 0, width, height)
    palette = b"".join(bytes((b, g, r, 0)) for r, g, b in PALETTE) if depth == 8 else b""
    strf = struct.pack("<IiiHH4sIiiII", 40, width, height, 1, depth, b"MSVC",
                      width * height * depth // 8, 0, 0, len(PALETTE) if depth == 8 else 0, 0) + palette
    header = chunk(b"LIST", b"hdrl" + chunk(b"avih", avih) +
                   chunk(b"LIST", b"strl" + chunk(b"strh", strh) + chunk(b"strf", strf)))
    offset, movie, index = 4, b"", b""
    for number, packet in enumerate(packets):
        body = chunk(b"00dc", packet)
        index += struct.pack("<4sIII", b"00dc", 0x10 if number % 3 == 0 else 0, offset, len(packet))
        movie += body
        offset += len(body)
    payload = b"AVI " + header + chunk(b"LIST", b"movi" + movie) + chunk(b"idx1", index)
    return b"RIFF" + struct.pack("<I", len(payload)) + payload


manifest = {"width": 64, "height": 48, "palette": PALETTE, "streams": []}
for depth in (8, 16):
    width, height = 64, 48
    blocks = width * height // 16
    tiles, packets, entries = [], [], []
    for frame in range(12):
        if frame % 3 == 0:
            encoded = [tile(depth, block % 3, block + frame) for block in range(blocks)]
            packet, tiles = b"".join(part[0] for part in encoded), [part[1] for part in encoded]
        else:
            changed = (frame * 17) % blocks
            opcode, pixels = tile(depth, frame % 3, frame)
            packet = skip(changed) + opcode + skip(blocks - changed - 1)
            tiles[changed] = pixels
        packet += b"\0\0"
        name = f"{depth}-frame-{frame:02d}"
        (TARGET / f"{name}.msv1").write_bytes(packet)
        (TARGET / f"{name}.rgba").write_bytes(rgba_from_tiles(tiles, width, height, depth))
        packets.append(packet)
        entries.append({"packet": f"{name}.msv1", "rgba": f"{name}.rgba", "keyframe": frame % 3 == 0})
    (ROOT / f"Luma-MSVideo1-{depth}.avi").write_bytes(avi(depth, packets, width, height))
    manifest["streams"].append({"bitDepth": depth, "frames": entries})
(TARGET / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
print("Generated original MS Video 1 8/16-bit AVI, all block modes, delta chains, and 24 exact RGBA goldens")
