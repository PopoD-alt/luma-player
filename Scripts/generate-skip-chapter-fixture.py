#!/usr/bin/env python3
"""Add authored, labelled chapter timing to Luma's synthetic 12-second MKV.

Only EBML container metadata is generated. Every encoded video packet remains
byte-identical to the original AVC/AAC test fixture. Its intentionally short AAC
track ends at 3.5 seconds while video lasts 12 seconds; omit that audio track so
chapter-clock tests cover the full video without a stale audio master clock.
Existing AVC/AAC fixtures continue to test audio separately. No external codec
or multimedia tool is needed.
"""
from pathlib import Path
import struct

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'Tests/Fixtures/Luma-AVC-AAC.mkv'
OUTPUT = ROOT / 'Tests/Fixtures/Luma-Skip-Chapters.mkv'

def vint(data, offset, identifier=False):
    first = data[offset]
    width = next((i for i in range(1, 9) if first & (1 << (8-i))), 0)
    if not width or offset + width > len(data):
        raise ValueError('Invalid bounded EBML value')
    raw = int.from_bytes(data[offset:offset+width], 'big')
    return (raw if identifier else raw & ((1 << (7*width))-1)), width

def elements(data):
    offset = 0
    while offset < len(data):
        start = offset
        identifier, iw = vint(data, offset, True); offset += iw
        size, sw = vint(data, offset); offset += sw
        if size == (1 << (7*sw))-1:
            size = len(data)-offset
        end = offset+size
        if end > len(data): raise ValueError('Truncated authored fixture')
        yield identifier, start, offset, end
        offset = end

def size(value):
    for width in range(1, 9):
        if value < (1 << (7*width))-1:
            return ((1 << (7*width)) | value).to_bytes(width, 'big')
    raise ValueError('Oversized EBML fixture')

def element(identifier, body):
    return identifier.to_bytes((identifier.bit_length()+7)//8, 'big') + size(len(body)) + body

def integer(identifier, value):
    return element(identifier, value.to_bytes(max(1,(value.bit_length()+7)//8),'big'))

def chapters():
    entries=[]
    for uid, (start, end, name) in enumerate([(0,1,'Cold open'),(1,3,'Opening'),(3,8,'Story'),(8,10,'Credits'),(10,12.25,'Post-credits')], 1):
        display=element(0x80,element(0x85,name.encode('utf-8'))+element(0x437C,b'eng'))
        body=integer(0x73C4,uid)+integer(0x91,round(start*1_000_000_000))+integer(0x92,round(end*1_000_000_000))+integer(0x98,0)+integer(0x4598,1)+display
        entries.append(element(0xB6,body))
    return element(0x1043A770,element(0x45B9,integer(0x45DB,1)+b''.join(entries)))

def video_only(payload):
    """Retain the authored AVC track and every unchanged video block."""
    video_tracks = set()
    for identifier, _, start, end in elements(payload):
        if identifier != 0x1654AE6B:
            continue
        for child, _, ts, te in elements(payload[start:end]):
            if child != 0xAE:
                continue
            entry = payload[start+ts:start+te]
            fields = {key: entry[begin:finish] for key, _, begin, finish in elements(entry)}
            if int.from_bytes(fields.get(0x83, b''), 'big') == 1:
                video_tracks.add(int.from_bytes(fields[0xD7], 'big'))
    if len(video_tracks) != 1:
        raise ValueError('Expected one authored video track')
    output = []
    for identifier, begin, start, end in elements(payload):
        body = payload[start:end]
        if identifier == 0x1549A966:
            # Local demux can infer a length, but HTTP needs explicit Info.Duration.
            # Duration is expressed in TimestampScale ticks, not nanoseconds.
            fields = list(elements(body))
            scales = [int.from_bytes(body[fs:fe], 'big') for key, _, fs, fe in fields if key == 0x2AD7B1]
            scale = scales[0] if scales else 1_000_000
            if not 1 <= scale <= 1_000_000_000:
                raise ValueError('Unexpected authored timestamp scale')
            retained = b''.join(body[fb:fe] for key, fb, _, fe in fields if key not in {0x4489, 0xBF})
            output.append(element(identifier, retained + element(0x4489, struct.pack('>d', 12.25 * 1_000_000_000 / scale))))
        elif identifier == 0x1654AE6B:
            entries = []
            for child, eb, es, ee in elements(body):
                if child != 0xAE:
                    continue
                entry = body[es:ee]
                fields = {key: entry[fs:fe] for key, _, fs, fe in elements(entry)}
                if int.from_bytes(fields.get(0xD7, b''), 'big') in video_tracks:
                    entries.append(body[eb:ee])
            output.append(element(identifier, b''.join(entries)))
        elif identifier == 0x1F43B675:
            children = []
            for child, cb, cs, ce in elements(body):
                contents = body[cs:ce]
                if child == 0xA3:
                    track, _ = vint(contents, 0)
                    if track not in video_tracks:
                        continue
                elif child == 0xA0:
                    blocks = [contents[bs:be] for key, _, bs, be in elements(contents) if key == 0xA1]
                    if not blocks or vint(blocks[0], 0)[0] not in video_tracks:
                        continue
                elif child in {0xA7, 0xAB, 0xBF}:
                    # Position/PrevSize/CRC refer to the old container layout.
                    continue
                children.append(body[cb:ce])
            output.append(element(identifier, b''.join(children)))
        elif identifier != 0xBF:
            output.append(payload[begin:end])
    return b''.join(output)

def main():
    data=SOURCE.read_bytes()
    if len(data)>64*1024*1024: raise ValueError('Source fixture too large')
    top=list(elements(data))
    if len(top)!=2 or top[0][0]!=0x1A45DFA3 or top[1][0]!=0x18538067:
        raise ValueError('Unexpected original fixture layout')
    _,_,body,end=top[1]
    payload=data[body:end]
    if any(i in {0x114D9B74,0x1C53BB6B,0x1043A770} for i,_,_,_ in elements(payload)):
        raise ValueError('Refuse offset-indexed or already chaptered source fixture')
    result=data[:top[1][1]]+element(0x18538067,chapters()+video_only(payload))
    OUTPUT.write_bytes(result)
    print(f'Generated {OUTPUT.relative_to(ROOT)} ({len(result)} bytes); all original encoded video blocks retained; partial audio omitted')

if __name__=='__main__': main()
