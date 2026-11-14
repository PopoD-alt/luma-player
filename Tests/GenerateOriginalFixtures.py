from pathlib import Path
import struct
import wave

output = Path(__file__).resolve().parent / 'Fixtures'
output.mkdir(exist_ok=True)
def chunk(tag, data):
    return tag + struct.pack('<I', len(data)) + data + (b'\0' if len(data) & 1 else b'')
def riff(kind, data): return b'RIFF' + struct.pack('<I', len(data) + 4) + kind + data
# Standard IMA WAVE block: predictor 0, step index 0, 504 one-valued nibbles.
# Each nibble adds exactly one while the index stays zero. 505 frames/block.
fmt = struct.pack('<HHIIHHHH', 0x11, 1, 8000, 4055, 256, 4, 2, 505)
block = struct.pack('<hBB', 0, 0, 0) + bytes([0x11]) * 252
(output / 'Luma-IMA.wav').write_bytes(riff(b'WAVE', chunk(b'fmt ', fmt) + chunk(b'fact', struct.pack('<I', 505 * 64)) + chunk(b'data', block * 64)))
fmt_stereo = struct.pack('<HHIIHHHH', 0x11, 2, 8000, 14222, 16, 4, 2, 9)
stereo = struct.pack('<hBBhBB', 0, 0, 0, 100, 0, 0) + bytes([0x11]) * 4 + bytes([0x99]) * 4
(output / 'Luma-IMA-Stereo.wav').write_bytes(riff(b'WAVE', chunk(b'fmt ', fmt_stereo) + chunk(b'data', stereo)))
for tag, name, values in [(6, 'ALaw', [0xd5, 0x55, 0xaa, 0x2a]), (7, 'MuLaw', [0xff, 0x7f, 0x80, 0x00])]:
    fmt = struct.pack('<HHIIHH', tag, 1, 8000, 8000, 1, 8)
    (output / ('Luma-' + name + '.wav')).write_bytes(riff(b'WAVE', chunk(b'fmt ', fmt) + chunk(b'data', bytes(values) * 8000)))
# RGB24 bottom-up AVI, 2fps, six seconds. No audio stream.
w, h, count, fps = 64, 48, 12, 2
frame_size = w * h * 3
avih = struct.pack('<14I', 500000, frame_size * fps, 0, 0, count, 0, 1, frame_size, w, h, 0, 0, 0, 0)
strh = struct.pack('<4s4sIHH8Ihhhh', b'vids', b'DIB ', 0, 0, 0, 0, 1, fps, 0, count, frame_size, 0xffffffff, 0, 0, 0, w, h)
strf = struct.pack('<IiiHHIIiiII', 40, w, h, 1, 24, 0, frame_size, 0, 0, 0, 0)
hdrl = chunk(b'LIST', b'hdrl' + chunk(b'avih', avih) + chunk(b'LIST', b'strl' + chunk(b'strh', strh) + chunk(b'strf', strf)))
colors = [bytes([0, 0, 255]), bytes([0, 255, 0]), bytes([255, 0, 0])]
frames = b''.join(chunk(b'00db', colors[i % 3] * (w * h)) for i in range(count))
(output / 'Luma-RGB.avi').write_bytes(riff(b'AVI ', hdrl + chunk(b'LIST', b'movi' + frames)))
print('Generated original codec fixtures')
# Standard RLE8 keyframes: each scanline is one run plus end-of-line.
palette = b''.join([bytes([0, 0, 255, 0]), bytes([0, 255, 0, 0]), bytes([255, 0, 0, 0])])
strh_rle = struct.pack('<4s4sIHH8Ihhhh', b'vids', b'MRLE', 0, 0, 0, 0, 1, fps, 0, count, frame_size, 0xffffffff, 0, 0, 0, w, h)
strf_rle = struct.pack('<IiiHHIIiiII', 40, w, h, 1, 8, 1, 0, 0, 0, 3, 0) + palette
hdrl_rle = chunk(b'LIST', b'hdrl' + chunk(b'avih', avih) + chunk(b'LIST', b'strl' + chunk(b'strh', strh_rle) + chunk(b'strf', strf_rle)))
compressed = b''.join(chunk(b'00dc', bytes([w, i % 3, 0, 0]) * h + bytes([0, 1])) for i in range(count))
(output / 'Luma-RLE8.avi').write_bytes(riff(b'AVI ', hdrl_rle + chunk(b'LIST', b'movi' + compressed)))
