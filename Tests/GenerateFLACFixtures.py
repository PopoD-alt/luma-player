#!/usr/bin/env python3
"""Original, small FLAC fixture encoder written from RFC 9639.

This is a deterministic test-data producer, not a production encoder. It uses
only Python's standard library and intentionally forces coding choices so that
golden tests exercise each decoder branch. No third-party codec code is used.
Specification: https://www.rfc-editor.org/rfc/rfc9639.html
"""

from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path


class Bits:
    def __init__(self):
        self.values = []

    def put(self, value, width):
        assert width >= 0
        value &= (1 << width) - 1
        self.values.extend((value >> bit) & 1 for bit in range(width - 1, -1, -1))

    def unary(self, zero_count):
        assert 0 <= zero_count < 1_000_000
        self.values.extend([0] * zero_count)
        self.values.append(1)

    def bytes(self):
        self.values.extend([0] * (-len(self.values) % 8))
        return bytes(sum(self.values[i + b] << (7 - b) for b in range(8)) for i in range(0, len(self.values), 8))


def crc(data, width, polynomial):
    value = 0
    mask = (1 << width) - 1
    for byte in data:
        value ^= byte << (width - 8)
        for _ in range(8):
            value = ((value << 1) ^ (polynomial if value & (1 << (width - 1)) else 0)) & mask
    return value


def coded_number(number):
    if number < 128:
        return bytes([number])
    for length, limit in [(2, 1 << 11), (3, 1 << 16), (4, 1 << 21), (5, 1 << 26), (6, 1 << 31), (7, 1 << 36)]:
        if number < limit:
            result = [0] * length
            for i in range(length - 1, 0, -1):
                result[i] = 0x80 | (number & 0x3F)
                number >>= 6
            result[0] = ((0xFF << (8 - length)) & 0xFF) | number
            return bytes(result)
    raise ValueError("FLAC coded number exceeds 36 bits")


FIXED = [[], [1], [2, -1], [3, -3, 1], [4, -6, 4, -1]]


def residual(writer, values, order, block_size, method=0, partition_order=0, parameters=None):
    writer.put(method, 2)
    writer.put(partition_order, 4)
    partitions = 1 << partition_order
    assert block_size % partitions == 0 and block_size // partitions > order
    position = 0
    for partition in range(partitions):
        count = block_size // partitions - (order if partition == 0 else 0)
        part = values[position:position + count]
        position += count
        setting = parameters[partition] if parameters else 3
        parameter_bits = 4 + method
        if isinstance(setting, tuple) and setting[0] == "escape":
            width = setting[1]
            writer.put((1 << parameter_bits) - 1, parameter_bits)
            writer.put(width, 5)
            assert all(-(1 << (width - 1)) <= v < (1 << (width - 1)) for v in part) if width else all(v == 0 for v in part)
            for value in part:
                writer.put(value, width)
        else:
            k = setting
            writer.put(k, parameter_bits)
            for value in part:
                assert -(1 << 31) < value < (1 << 31)
                folded = value * 2 if value >= 0 else -value * 2 - 1
                writer.unary(folded >> k)
                writer.put(folded, k)
    assert position == len(values)


def subframe(writer, samples, bit_depth, kind="verbatim", order=0, coefficients=None, precision=5, shift=0, wasted=0, **rice):
    assert wasted < bit_depth
    reduced = [value >> wasted for value in samples]
    assert all(value << wasted == original for value, original in zip(reduced, samples))
    depth = bit_depth - wasted
    if kind == "constant":
        code = 0
        assert len(set(reduced)) == 1
    elif kind == "verbatim":
        code = 1
    elif kind == "fixed":
        code = 8 + order
        coefficients = FIXED[order]
    elif kind == "lpc":
        order = len(coefficients)
        code = 31 + order
    else:
        raise ValueError(kind)
    writer.put(0, 1)
    writer.put(code, 6)
    writer.put(bool(wasted), 1)
    if wasted:
        writer.unary(wasted - 1)
    if kind == "constant":
        writer.put(reduced[0], depth)
        return
    if kind == "verbatim":
        for value in reduced:
            writer.put(value, depth)
        return
    for value in reduced[:order]:
        writer.put(value, depth)
    if kind == "lpc":
        assert 1 <= precision <= 15 and 0 <= shift <= 15
        writer.put(precision - 1, 4)
        writer.put(shift, 5)
        for coefficient in coefficients:
            writer.put(coefficient, precision)
    errors = []
    for index in range(order, len(reduced)):
        estimate = sum(coefficient * reduced[index - j - 1] for j, coefficient in enumerate(coefficients)) >> shift
        errors.append(reduced[index] - estimate)
    residual(writer, errors, order, len(samples), **rice)


DEPTH_CODES = {8: 1, 12: 2, 16: 4, 20: 5, 24: 6, 32: 7}
RATE_CODES = {8000: 4, 16000: 5, 22050: 6, 24000: 7, 32000: 8, 44100: 9, 48000: 10, 96000: 11}


def frame(channels, bits_per_sample, rate, settings, number=0, variable=False, assignment=None, rate_style="standard", depth_from_streaminfo=False, block_style="explicit"):
    count = len(channels[0])
    assert all(len(channel) == count for channel in channels)
    assignment = len(channels) - 1 if assignment is None else assignment
    stored_channels = channels
    depths = [bits_per_sample] * len(channels)
    if assignment in (8, 9, 10):
        left, right = channels
        side = [l - r for l, r in zip(left, right)]
        if assignment == 8:
            stored_channels, depths = [left, side], [bits_per_sample, bits_per_sample + 1]
        elif assignment == 9:
            stored_channels, depths = [side, right], [bits_per_sample + 1, bits_per_sample]
        else:
            stored_channels = [[(l + r) >> 1 for l, r in zip(left, right)], side]
            depths = [bits_per_sample, bits_per_sample + 1]
    if block_style == "standard":
        block_code = {192: 1, 256: 8, 512: 9}[count]
        block_extra = b""
    else:
        block_code = 6 if count <= 256 else 7
        block_extra = (count - 1).to_bytes(1 if block_code == 6 else 2, "big")
    if rate_style == "streaminfo":
        rate_code, rate_extra = 0, b""
    elif rate_style == "khz":
        rate_code, rate_extra = 12, (rate // 1000).to_bytes(1, "big")
    elif rate_style == "hz":
        rate_code, rate_extra = 13, rate.to_bytes(2, "big")
    elif rate_style == "tens":
        rate_code, rate_extra = 14, (rate // 10).to_bytes(2, "big")
    else:
        rate_code, rate_extra = RATE_CODES[rate], b""
    depth_code = 0 if depth_from_streaminfo else DEPTH_CODES.get(bits_per_sample, 0)
    header = bytes([0xFF, 0xF9 if variable else 0xF8, (block_code << 4) | rate_code, (assignment << 4) | (depth_code << 1)]) + coded_number(number) + block_extra + rate_extra
    header += bytes([crc(header, 8, 0x07)])
    writer = Bits()
    for channel, depth, setting in zip(stored_channels, depths, settings):
        subframe(writer, channel, depth, **setting)
    body = header + writer.bytes()
    return body + crc(body, 16, 0x8005).to_bytes(2, "big")


def encode(blocks, bits_per_sample=16, rate=44100, variable=False, assignment=None, rate_style="standard", depth_from_streaminfo=False, block_style="explicit", extra_metadata=False, md5=True):
    frames = []
    samples = []
    sample_number = 0
    for index, (channels, settings) in enumerate(blocks):
        frames.append(frame(channels, bits_per_sample, rate, settings, sample_number if variable else index, variable, assignment, rate_style, depth_from_streaminfo, block_style))
        sample_number += len(channels[0])
        samples.extend(value for simultaneous in zip(*channels) for value in simultaneous)
    channel_count = len(blocks[0][0])
    block_sizes = [len(block[0][0]) for block in blocks]
    pcm = b"".join(value.to_bytes((bits_per_sample + 7) // 8, "little", signed=True) for value in samples)
    streaminfo = min(block_sizes).to_bytes(2, "big") + max(block_sizes).to_bytes(2, "big")
    streaminfo += min(map(len, frames)).to_bytes(3, "big") + max(map(len, frames)).to_bytes(3, "big")
    streaminfo += ((rate << 44) | ((channel_count - 1) << 41) | ((bits_per_sample - 1) << 36) | sample_number).to_bytes(8, "big")
    streaminfo += hashlib.md5(pcm).digest() if md5 else bytes(16)
    metadata = bytes([0 if extra_metadata else 0x80, 0, 0, 34]) + streaminfo
    if extra_metadata:
        metadata += bytes([1, 0, 0, 7]) + bytes(7)
        comment = (4).to_bytes(4, "little") + b"Luma" + (0).to_bytes(4, "little")
        metadata += bytes([0x84]) + len(comment).to_bytes(3, "big") + comment
    return b"fLaC" + metadata + b"".join(frames), samples


def main():
    directory = Path(__file__).resolve().parent / "Fixtures" / "flac"
    directory.mkdir(parents=True, exist_ok=True)
    manifest = []

    def fixture(name, blocks, features, **options):
        data, samples = encode(blocks, **options)
        filename = name + ".flac"
        (directory / filename).write_bytes(data)
        manifest.append({"name": name, "file": filename, "sampleRate": options.get("rate", 44100), "channels": len(blocks[0][0]), "bitsPerSample": options.get("bits_per_sample", 16), "samples": samples, "features": features})

    constant = [{"kind": "constant"}]
    verbatim = [{"kind": "verbatim"}]
    fixture("constant-8", [([[-37] * 32], constant)], ["constant", "8-bit"] , bits_per_sample=8)
    fixture("verbatim-16", [([[(-1 if i % 2 else 1) * (i * 811 % 32000) for i in range(32)]], verbatim)], ["verbatim", "16-bit", "metadata"], extra_metadata=True)
    for depth in (4, 7, 12, 20, 24, 31, 32):
        maximum = (1 << (depth - 1)) - 1
        values = [-maximum - 1, maximum, -1, 0, 1, maximum // 2, -maximum // 2, 2] * 4
        fixture(f"verbatim-{depth}", [([values], verbatim)], ["verbatim", f"{depth}-bit"], bits_per_sample=depth, depth_from_streaminfo=depth not in DEPTH_CODES)
    for order in range(5):
        values = [((i - 10) ** min(order, 3)) + (i % 3) - 1 for i in range(32)]
        fixture(f"fixed-order-{order}", [([values], [{"kind": "fixed", "order": order, "parameters": [8]}])], ["fixed", f"order-{order}", "rice0"])
    values = [int(100 * i + 5 * (i % 4)) for i in range(32)]
    fixture("fixed-rice1-partitions", [([values], [{"kind": "fixed", "order": 2, "method": 1, "partition_order": 2, "parameters": [5, 4, 6, 7]}])], ["fixed", "rice1", "partitions"])
    values = [0, 3, -2, 5, -5, 0, 1, -1] * 4
    fixture("fixed-escape-partitions", [([values], [{"kind": "fixed", "order": 0, "partition_order": 2, "parameters": [("escape", 4)] * 4}])], ["fixed", "escape", "partitions"])
    fixture("fixed-escape-zero", [([[0] * 32], [{"kind": "fixed", "order": 0, "parameters": [("escape", 0)]}])], ["fixed", "escape-zero"])
    fixture("rice0-parameter-zero", [([[0, -1, 1, 0] * 8], [{"kind": "fixed", "order": 0, "parameters": [0]}])], ["fixed", "rice0", "zero-parameter"])
    fixture("rice1-parameter30", [([[0, -1, 1, -7] * 8], [{"kind": "fixed", "order": 0, "method": 1, "parameters": [30]}])], ["fixed", "rice1", "large-parameter"])
    values = [((i - 15) * 16) for i in range(32)]
    fixture("fixed-wasted-bits", [([values], [{"kind": "fixed", "order": 1, "wasted": 4, "parameters": [2]}])], ["fixed", "wasted-bits"])
    values = [i * i - 100 for i in range(32)]
    fixture("lpc-order2", [([values], [{"kind": "lpc", "coefficients": [2, -1], "precision": 4, "parameters": [2]}])], ["lpc", "negative-coefficient"])
    fixture("lpc-shift-rice1", [([values], [{"kind": "lpc", "coefficients": [3, -1], "precision": 4, "shift": 1, "method": 1, "partition_order": 1, "parameters": [8, 8]}])], ["lpc", "shift", "rice1", "partitions"])
    # Order 32 is valid at the upper end of FLAC's generic LPC range.
    long_values = [i * 2 - 40 for i in range(64)]
    fixture("lpc-order32", [([long_values], [{"kind": "lpc", "coefficients": [1] + [0] * 31, "precision": 2, "parameters": [2]}])], ["lpc", "order-32"])
    left = [(-1 if i % 2 else 1) * (i * 391 + 13) for i in range(32)]
    right = [l + (i % 5) - 2 for i, l in enumerate(left)]
    for assignment, name in [(1, "independent"), (8, "left-side"), (9, "side-right"), (10, "mid-side")]:
        fixture(f"stereo-{name}", [([left, right], verbatim * 2)], ["stereo", name], assignment=assignment)
    # Extreme 32-bit stereo exercises the 33-bit decorrelated side channel.
    left32 = [-(1 << 31), (1 << 31) - 1, -1, 1] * 8
    right32 = [(1 << 31) - 1, -(1 << 31), 1, -1] * 8
    fixture("stereo-mid-side-32", [([left32, right32], verbatim * 2)], ["stereo", "mid-side", "33-bit-side"], bits_per_sample=32, assignment=10)
    blocks = []
    for index, count in enumerate([16, 20, 24, 32, 40, 48, 56, 64]):
        blocks.append(([[index * 50 + i for i in range(count)]], [{"kind": "fixed", "order": 1, "parameters": [1]}]))
    fixture("variable-blocks-utf8", blocks, ["variable-blocks", "multiframe", "utf8-number"], variable=True)
    blocks = [([[index + i for i in range(16)]], verbatim) for index in range(130)]
    fixture("fixed-frames-utf8", blocks, ["fixed-blocks", "multiframe", "utf8-frame-number"])
    for style, rate in [("streaminfo", 12345), ("khz", 22000), ("hz", 54321), ("tens", 100000)]:
        fixture(f"rate-{style}", [([[i - 16 for i in range(32)]], verbatim)], ["sample-rate", style], rate=rate, rate_style=style)
    for count in (192, 256, 512):
        fixture(f"block-code-{count}", [([[7] * count], constant)], ["standard-block-code"], block_style="standard")
    fixture("explicit-block16", [([[3] * 257], constant)], ["16-bit-block-size"])
    fixture("six-channels", [([[i * 7 - 100 + channel * 19 for i in range(32)] for channel in range(6)], verbatim * 6)], ["multichannel", "independent"])
    fixture("unspecified-md5", [([[i for i in range(32)]], verbatim)], ["unspecified-md5"], md5=False)

    # Published test-data examples, not implementation source: RFC 9639,
    # Appendix D.1 and D.3. Exact byte strings and stated sample values provide
    # an oracle independent of the synthetic encoder above.
    published = [
        ("rfc9639-example1", "664c6143800000221000100000000f00000f0ac442f0000000013e84b41807dc690307586a3dad1a2e0ffff869180000bf0358fd03128baa9a", 44100, 2, 16, [25588, 10416]),
        ("rfc9639-example3", "664c6143800000221000100000001f00001f07d0007000000018f8f9e396f5cbcfc6dc807f9977906b32fff868020017e944004f6f313d1047d227cb6d090831452bdc2822228057a3", 32000, 1, 8, [0, 79, 111, 78, 8, -61, -90, -68, -13, 42, 67, 53, 13, -27, -46, -38, -12, 14, 24, 19, 6, -4, -5, 0]),
    ]
    for name, hex_data, rate, channels, depth, samples in published:
        filename = name + ".flac"
        (directory / filename).write_bytes(bytes.fromhex(hex_data))
        manifest.append({"name": name, "file": filename, "sampleRate": rate, "channels": channels, "bitsPerSample": depth, "samples": samples, "features": ["published-rfc9639-example", "short-final-block"], "source": "https://www.rfc-editor.org/rfc/rfc9639.html#appendix-D"})
    (directory / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"Generated {len(manifest)} deterministic FLAC fixtures in {directory}")


def playback_silence_fixture():
    """Twelve seconds leaves enough transport-test time after a 1.5s seek."""
    path = Path(__file__).resolve().parent / "Fixtures" / "Luma-Silence.flac"
    blocks = [([[0] * 4800, [0] * 4800], [{"kind": "constant"}] * 2) for _ in range(120)]
    encoded, _ = encode(blocks, bits_per_sample=16, rate=48000)
    path.write_bytes(encoded)
    print(f"Generated {path}: 12 seconds, stereo 48000 Hz / 16-bit, {len(encoded)} bytes")


if __name__ == "__main__":
    if sys.argv[1:] != ["--playback-silence-only"]:
        main()
    playback_silence_fixture()
