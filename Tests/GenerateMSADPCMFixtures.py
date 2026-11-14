#!/usr/bin/env python3
"""Original Microsoft ADPCM test blocks assembled from Microsoft's RIFF layout.

No compressed codec library, VLC, FFmpeg, or downloaded media is used. Expected
samples below are independent arithmetic vectors, not output from Luma's decoder.
Primary layout/algorithm: Microsoft Multimedia Data Standards Update, rev3 (1994),
pp22–26: https://elm-chan.org/junk/adpcm/RIFF_NEW.pdf
"""
from pathlib import Path
import hashlib
import json
import struct
import sys

ROOT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent / "Fixtures" / "ms-adpcm"
ROOT.mkdir(parents=True, exist_ok=True)
STANDARD = [(256, 0), (512, -256), (0, 0), (192, 64), (240, 0), (460, -208), (392, -232)]


def chunk(name, payload):
    return name + struct.pack("<I", len(payload)) + payload + bytes(len(payload) & 1)


def block(predictors, deltas, newest, oldest, payload, size=None):
    result = bytes(predictors)
    result += b"".join(struct.pack("<H", x) for x in deltas)
    result += b"".join(struct.pack("<h", x) for x in newest)
    result += b"".join(struct.pack("<h", x) for x in oldest)
    result += bytes(payload)
    if size is not None:
        assert len(result) <= size
        result += bytes(size - len(result))
    return result


def wave(channels, alignment, samples_per_block, coefficients, blocks, fact, rate=8000):
    extra = struct.pack("<HH", samples_per_block, len(coefficients))
    extra += b"".join(struct.pack("<hh", *pair) for pair in coefficients)
    fmt = struct.pack("<HHIIHHH", 2, channels, rate, rate * alignment // samples_per_block,
                      alignment, 4, len(extra)) + extra
    body = b"WAVE" + chunk(b"fmt ", fmt) + chunk(b"fact", struct.pack("<I", fact)) + chunk(b"data", blocks)
    return b"RIFF" + struct.pack("<I", len(body)) + body


vectors = []


def save(name, channels, alignment, coefficients, encoded, expected, explanation, native_reference=True):
    per_block = 2 + (alignment - 7 * channels) * 2 // channels
    frame_count = len(expected) // channels
    wav = wave(channels, alignment, per_block, coefficients, encoded, frame_count)
    (ROOT / (name + ".wav")).write_bytes(wav)
    (ROOT / (name + ".blocks")).write_bytes(encoded)
    pcm = struct.pack("<" + "h" * len(expected), *expected)
    (ROOT / (name + ".pcm")).write_bytes(pcm)
    vectors.append({"name": name, "channels": channels, "sample_rate": 8000,
                    "block_align": alignment, "samples_per_block": per_block,
                    "coefficients": coefficients, "sample_count": frame_count,
                    "expected_samples": expected, "expected_pcm_sha256": hashlib.sha256(pcm).hexdigest(),
                    "native_reference_requested": native_reference, "explanation": explanation})


save("mono-ramp-fact", 1, 256, STANDARD,
     block([0], [16], [100], [84], [0x11] * 249),
     [84 + 16 * n for n in range(497)],
     "Predictor0 repeats the newest sample; each nibble1 adds16. fact trims three final samples.")
save("stereo-opposite-fact", 2, 256, STANDARD,
     block([0, 0], [16, 16], [100, -100], [84, -84], [0x1f] * 242),
     [sample for n in range(241) for sample in (84 + 16 * n, -84 - 16 * n)],
     "High nibble1 advances left, low nibble-1 advances right independently; fact trims three frames.")
save("linear-predictor", 1, 256, STANDARD,
     block([1], [16], [10], [0], [], 256),
     [10 * n for n in range(500)],
     "Predictor1 extrapolates two seeds linearly; all correction nibbles are zero.")
save("adaptation-and-sign", 1, 256, STANDARD,
     block([2], [16], [7], [-3], [0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef], 256),
     [-3, 7, 0, 16, 32, 48, 64, 95, 180, 420, -1144, -3003, -6168, -10280, -13136, -11814, -7076, -3178],
     "Zero predictor isolates signed nibbles0..15, adaptation factors, floor16 and saturation-free scaling.")
save("clip-both-signs", 1, 256, STANDARD,
     block([0], [32767], [32000], [31000], [0x78], 256),
     [31000, 32000, 32767, -32768],
     "Large positive and negative corrections saturate; the saturated result becomes history.")
save("negative-fraction", 1, 256, STANDARD,
     block([3], [16], [-1], [0], [], 256),
     [0, -1, -1, -1, -1, -1, -1, -1],
     "Signed8.8 prediction -192/256 rounds downward, independently confirmed by Apple's native decoder.")
save("custom-coefficient", 1, 256, STANDARD + [(0, 256)],
     block([7], [16], [-25], [75], [], 256),
     [75, -25, 75, -25, 75, -25, 75, -25],
     "Appended predictor7 selects the older sample only, proving the custom table is actually used.")
save("partial-final-block", 1, 256, STANDARD,
     block([0], [16], [100], [84], [0x11] * 249)
     + block([0], [16], [1016], [1000], [0x11, 0x11]),
     [84 + 16 * n for n in range(500)] + [1000, 1016, 1032, 1048, 1064, 1080],
     "One complete block and one short final block with a complete header; fact counts only available samples.", False)
save("zero-initial-delta", 1, 256, STANDARD,
     block([0], [0], [100], [84], [0x11] * 249),
     [84, 100, 100, 116, 132, 148],
     "The header delta is used for the first correction; minimum16 applies after adaptation.")
save("signed-initial-delta", 1, 256, STANDARD,
     block([0], [65535], [100], [84], [0x11] * 249),
     [84, 100, 99, 115, 131, 147],
     "Initial delta0xFFFF is signed-1; adaptation then restores the positive minimum16.")

manifest = {"schema_version": 1, "provenance": "Original hand-assembled MS ADPCM bitstreams with independent arithmetic golden samples.",
            "generator": "Tests/GenerateMSADPCMFixtures.py", "vectors": vectors}
(ROOT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
print(f"Generated {len(vectors)} original Microsoft ADPCM vectors in {ROOT}")
