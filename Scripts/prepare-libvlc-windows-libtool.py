#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Original build support; see ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt.
"""Keep the pinned Clang compiler runtime in libtool's C++ DLL dependencies.

GNU libtool 2.5.4 omits absolute compiler-rt archive names from its hidden
dependency scan, then uses -nostdlib for MinGW C++ DLL links. This is the same
narrow archive-name recognition documented by GNU bug27866 and VideoLAN's
extras/tools/libtool-2.4.7-clang-libs.patch. No runtime routine is implemented
or substituted here. Run autoconf after updating the generated m4 file.
"""
import argparse
import hashlib
import json
from pathlib import Path


PREFIX = '  for p in `eval "$output_verbose_link_cmd"`; do\n    case $prev$p in\n\n'
OLD = PREFIX + '    -L* | -R* | -l*)'
NEW = PREFIX + '    -L* | -R* | -l* | */libclang_rt.*.a)'


def patch_macro(path: Path) -> dict:
    if not path.is_file() or path.is_symlink() or path.stat().st_size > 2 * 1024 * 1024:
        raise ValueError("Expected a bounded regular libtool.m4 file")
    before = path.read_bytes()
    text = before.decode("utf-8")
    if text.count(OLD) == 1 and NEW not in text:
        after = text.replace(OLD, NEW, 1).encode("utf-8")
        path.write_bytes(after)
        changed = True
    elif text.count(NEW) == 1 and OLD not in text:
        after = before
        changed = False
    else:
        raise ValueError("Unknown or ambiguous libtool hidden-dependency parser; refusing to patch")
    return {"change": "retain absolute Clang compiler-rt archives in hidden C++ dependencies",
            "changed": changed, "inputSHA256": hashlib.sha256(before).hexdigest(),
            "outputSHA256": hashlib.sha256(after).hexdigest(),
            "reference": "https://debbugs.gnu.org/cgi/bugreport.cgi?bug=27866"}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("macro", type=Path)
    parser.add_argument("--receipt", type=Path)
    args = parser.parse_args()
    receipt = patch_macro(args.macro)
    encoded = json.dumps(receipt, indent=2) + "\n"
    if args.receipt:
        args.receipt.write_text(encoded)
    print(encoded, end="")


if __name__ == "__main__":
    main()
