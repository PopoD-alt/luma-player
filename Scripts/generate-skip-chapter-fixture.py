#!/usr/bin/env python3
"""Add authored, labelled chapter timing to Luma's synthetic 12-second MKV.

Only EBML container metadata is generated. Encoded media payloads remain byte
identical to the repository's original AVC/AAC test fixture; no external codec
or multimedia tool is needed.
"""
from pathlib import Path

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
    result=data[:top[1][1]]+element(0x18538067,chapters()+payload)
    OUTPUT.write_bytes(result)
    print(f'Generated {OUTPUT.relative_to(ROOT)} ({len(result)} bytes); all original segment media bytes retained')

if __name__=='__main__': main()
