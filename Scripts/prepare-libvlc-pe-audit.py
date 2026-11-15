#!/usr/bin/env python3
"""Read x64 plugin license exports without executing DLLs. Metadata is not legal clearance.

Original bounded PE reader follows https://learn.microsoft.com/windows/win32/debug/pe-format.
The recognized function is only the x64 LEA-RAX,RIP+disp32;RET constant-string form
emitted for VideoLAN's VLC_META_EXPORT macro. Other code is reported unclassified.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import zipfile


class Pe:
    def __init__(self, data):
        self.data = data
        if len(data) > 128 * 1024 * 1024 or self.bytes(0, 2) != b"MZ":
            raise ValueError("Invalid/oversized DLL")
        header = self.u32(0x3c)
        if self.bytes(header, 4) != b"PE\0\0" or self.u16(header + 4) != 0x8664:
            raise ValueError("Expected x64 PE")
        count, size = self.u16(header + 6), self.u16(header + 20)
        optional = header + 24
        if not 1 <= count <= 96 or size < 120 or self.u16(optional) != 0x20b:
            raise ValueError("Invalid PE32+ header")
        self.headers = self.u32(optional + 60)
        self.sections = []
        for index in range(count):
            start = optional + size + index * 40
            virtual_size, virtual, raw_size, raw = struct.unpack("<IIII", self.bytes(start + 8, 16))
            self.bytes(raw, raw_size)
            self.sections.append((virtual, raw_size, raw))
        self.export_rva, self.export_size = struct.unpack("<II", self.bytes(optional + 112, 8))

    def bytes(self, offset, size):
        if offset < 0 or size < 0 or offset + size > len(self.data):
            raise ValueError("PE range outside file")
        return self.data[offset:offset + size]

    def u16(self, offset):
        return struct.unpack("<H", self.bytes(offset, 2))[0]

    def u32(self, offset):
        return struct.unpack("<I", self.bytes(offset, 4))[0]

    def offset(self, rva, size=1):
        if rva < self.headers:
            self.bytes(rva, size)
            return rva
        found = [raw + rva - virtual for virtual, raw_size, raw in self.sections if virtual <= rva and rva + size <= virtual + raw_size]
        if len(found) != 1:
            raise ValueError("Unmapped/ambiguous PE RVA")
        return found[0]

    def string(self, rva):
        result = bytearray()
        for index in range(2048):
            value = self.bytes(self.offset(rva + index), 1)[0]
            if value == 0:
                return result.decode("ascii")
            result.append(value)
        raise ValueError("Unterminated PE metadata string")

    def exports(self):
        if not self.export_rva:
            return {}
        table = self.offset(self.export_rva, 40)
        functions, names, function_table, name_table, ordinal_table = struct.unpack("<IIIII", self.bytes(table + 20, 20))
        if functions > 65536 or names > functions:
            raise ValueError("Oversized PE export table")
        result = {}
        for index in range(names):
            name_rva = self.u32(self.offset(name_table + index * 4, 4))
            ordinal = self.u16(self.offset(ordinal_table + index * 2, 2))
            if ordinal >= functions:
                raise ValueError("Invalid export ordinal")
            result[self.string(name_rva)] = self.u32(self.offset(function_table + ordinal * 4, 4))
        return result

    def constant_export(self, exports, prefix):
        names = [name for name in exports if name.startswith(prefix)]
        if len(names) != 1:
            return None
        rva = exports[names[0]]
        if self.export_rva <= rva < self.export_rva + self.export_size:
            return None  # Forwarded export, not a local function.
        code = self.bytes(self.offset(rva, 8), 8)
        if code[:3] != b"\x48\x8d\x05" or code[7] != 0xc3:
            return None
        displacement = struct.unpack("<i", code[3:7])[0]
        return self.string(rva + 7 + displacement)


def audit(archive):
    rows = []
    with zipfile.ZipFile(archive) as package:
        for item in package.infolist():
            if not item.filename.startswith("build/x64/plugins/") or not item.filename.endswith(".dll"):
                continue
            data = package.read(item)
            image = Pe(data)
            exports = image.exports()
            license_ = image.constant_export(exports, "vlc_entry_license__")
            classification = "LGPL-metadata" if license_ and "GNU Lesser General Public License" in license_ else "GPL-metadata" if license_ and "GNU General Public License" in license_ else "unclassified"
            rows.append({"path": item.filename, "sha256": hashlib.sha256(data).hexdigest(), "classification": classification,
                         "licenseExport": license_, "copyrightExport": image.constant_export(exports, "vlc_entry_copyright__")})
    return {"packageSha256": hashlib.sha256(Path(archive).read_bytes()).hexdigest(), "scope": "Exact plugin metadata exports; does not prove every source/dependency license or authorize redistribution.",
            "plugins": rows, "counts": {value: sum(row["classification"] == value for row in rows) for value in ("LGPL-metadata", "GPL-metadata", "unclassified")}}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = audit(args.archive)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result["counts"]))
