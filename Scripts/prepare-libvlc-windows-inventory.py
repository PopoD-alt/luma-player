#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 sdas
# See ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt.
"""Collect exact completed Windows contrib sources and verbatim notices.

This does not approve binaries. It never downloads, builds, deletes an existing
output, or substitutes a nearby dependency version for a missing source.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import sys
import tarfile
import tempfile


def digest(path: Path, algorithm: str = "sha256") -> str:
    with path.open("rb") as file:
        return hashlib.file_digest(file, algorithm).hexdigest()


def tree(root: Path) -> dict:
    result = {}
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise ValueError("Inventory output must not contain symlinks")
        if path.is_file():
            result[path.relative_to(root).as_posix()] = digest(path)
    return result


def collect(work: Path, output: Path, toolchain_license: Path | None) -> dict:
    source = work / "vlc-6de05adcba"
    build = source / "contrib/luma-windows-x64"
    cache = source / "contrib/tarballs"
    packages = sorted(p.name.removeprefix(".sum-") for p in build.glob(".sum-*"))
    if len(packages) != 25:
        raise ValueError("Expected the exact 25-package completed Windows profile")
    if toolchain_license is None:
        candidates = list(work.glob("llvm-mingw-*/LICENSE.TXT"))
        if len(candidates) != 1:
            raise ValueError("Supply --toolchain-license for the actual LLVM-MinGW compiler")
        toolchain_license = candidates[0]
    if not toolchain_license.is_file():
        raise ValueError("Actual compiler/runtime license is missing")
    output.parent.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix=".windows-source-inventory-", dir=output.parent))
    try:
        records = {}
        aliases = []
        for package in packages:
            if package == "winpthreads":
                if "mingw64" not in packages:
                    raise ValueError("Winpthreads source alias is missing MinGW-w64")
                aliases.append({"recipePackage": package, "sourceRecipePackage": "mingw64",
                                "reason": "Winpthreads is built from the same pinned MinGW-w64 archive."})
                continue
            sums = source / "contrib/src" / package / "SHA512SUMS"
            if not sums.is_file():
                raise ValueError("Missing source checksum recipe: " + package)
            matches = []
            for line in sums.read_text().splitlines():
                match = re.fullmatch(r"([a-fA-F0-9]{128})\s+\*?(\S+)", line.strip())
                if not match:
                    continue
                name = match[2]
                if Path(name).name != name:
                    raise ValueError("Unsafe source archive filename")
                archive = cache / name
                if not archive.is_file():
                    continue
                if digest(archive, "sha512") != match[1].lower():
                    raise ValueError("Source differs from its exact recipe checksum: " + name)
                matches.append(archive)
                notices = []
                with tarfile.open(archive) as tar:
                    for member in tar:
                        if not member.isfile() or not re.search(
                            r"(^|/)(copying[^/]*|licen[cs]e[^/]*|copyright[^/]*|notice[^/]*|authors[^/]*)$",
                            member.name, re.I):
                            continue
                        path = PurePosixPath(member.name)
                        if path.is_absolute() or ".." in path.parts or "\\" in member.name or member.size > 2 * 1024 * 1024:
                            raise ValueError("Unsafe or oversized source notice")
                        target = stage / "Notices/contrib" / name / path
                        target.parent.mkdir(parents=True, exist_ok=True)
                        value = tar.extractfile(member).read()
                        target.write_bytes(value)
                        notices.append({"path": path.as_posix(), "bytes": len(value),
                                        "sha256": hashlib.sha256(value).hexdigest()})
                records[name] = {"filename": name, "bytes": archive.stat().st_size,
                                 "sha256": digest(archive), "sha512": match[1].lower(),
                                 "recipePackage": package, "verifiedAgainstUpstreamRecipe": True,
                                 "notices": notices}
            if len(matches) != 1:
                raise ValueError("Expected one exact source archive for " + package)
        if len(records) != 24:
            raise ValueError("Expected exactly 24 source archives for this pinned profile")
        vlc_notices = stage / "Notices/VLC"
        vlc_notices.mkdir(parents=True)
        for name in ("COPYING", "COPYING.LIB", "AUTHORS", "THANKS"):
            shutil.copyfile(source / name, vlc_notices / name)
        compiler_notices = stage / "Notices/LLVM-MinGW"
        compiler_notices.mkdir(parents=True)
        shutil.copyfile(toolchain_license, compiler_notices / "LICENSE.TXT")
        result = {"schema": 1, "sourceCommit": "6de05adcba", "selectedRecipePackages": packages,
                  "selectedPackageCount": len(packages), "sourceAliases": aliases,
                  "archiveCount": len(records), "archives": list(records.values()), "issues": [],
                  "noticeFiles": sum(len(item["notices"]) for item in records.values()),
                  "totalSourceBytes": sum(item["bytes"] for item in records.values()),
                  "toolchainLicenseSha256": digest(toolchain_license),
                  "scope": "Completed exact source recipe inputs and verbatim notices. Compiler/toolchain provenance, module/core/dependency review and native execution are separate; this is not redistribution approval or legal clearance."}
        (stage / "source-inventory.json").write_text(json.dumps(result, indent=2) + "\n")
        if output.exists():
            if not output.is_dir() or tree(output) != tree(stage):
                raise ValueError("Existing output differs; choose a fresh output directory")
        else:
            os.replace(stage, output)
        return {"output": str(output.resolve()), "sourcePackages": len(packages),
                "archives": len(records), "noticeFiles": result["noticeFiles"],
                "sourceBytes": result["totalSourceBytes"], "status": "inventoried-not-approved"}
    finally:
        if stage.exists():
            shutil.rmtree(stage)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--toolchain-license", type=Path)
    arguments = parser.parse_args()
    try:
        print(json.dumps(collect(arguments.work, arguments.output, arguments.toolchain_license), indent=2))
        return 0
    except (OSError, ValueError, tarfile.TarError) as error:
        print("Windows source inventory failed: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
