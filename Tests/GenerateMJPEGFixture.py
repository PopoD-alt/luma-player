from pathlib import Path
import struct
root = Path(__file__).resolve().parent / 'Fixtures'
jpeg = (root / 'Luma-Baseline.jpg').read_bytes()
offset = jpeg.index(b'\xff\xc0')
height, width = struct.unpack_from('>HH', jpeg, offset + 5)
def chunk(tag, data): return tag + struct.pack('<I', len(data)) + data + (b'\0' if len(data) & 1 else b'')
fps, count = 2, 12
avih = struct.pack('<14I', 500000, len(jpeg) * fps, 0, 0, count, 0, 1, len(jpeg), width, height, 0, 0, 0, 0)
strh = struct.pack('<4s4sIHH8Ihhhh', b'vids', b'MJPG', 0, 0, 0, 0, 1, fps, 0, count, len(jpeg), 0xffffffff, 0, 0, 0, width, height)
strf = struct.pack('<IiiHH4sIiiII', 40, width, height, 1, 24, b'MJPG', len(jpeg), 0, 0, 0, 0)
headers = chunk(b'LIST', b'hdrl' + chunk(b'avih', avih) + chunk(b'LIST', b'strl' + chunk(b'strh', strh) + chunk(b'strf', strf)))
frames = chunk(b'LIST', b'movi' + b''.join(chunk(b'00dc', jpeg) for _ in range(count)))
payload = b'AVI ' + headers + frames
(root / 'Luma-MJPEG.avi').write_bytes(b'RIFF' + struct.pack('<I', len(payload)) + payload)
print('Generated independent MJPEG AVI container with system-encoded JPEG fixture')
