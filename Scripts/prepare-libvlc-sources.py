#!/usr/bin/env python3
"""Collect pinned upstream source inputs; never asserts redistribution approval."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tarfile

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("libvlc_prepare", Path(__file__).with_name("prepare-libvlc.py"))
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)
REPO = prepare.REPO
WORK = REPO / "ThirdParty/work"
VLC = WORK / "vlc-79128878"
KIT = WORK / "vlckit-319ed2c0"
EVIDENCE = REPO / "ThirdParty/libvlc"


def setup() -> None:
    record = prepare.package("macos-vlckit-3.7.3")
    for source, destination, prefix in zip(record["sources"], (KIT, VLC), ("vlckit-319ed2c0/", "vlc-79128878/")):
        archive = prepare.obtain(source, REPO / "ThirdParty/downloads")
        if not destination.exists():
            destination.mkdir(parents=True)
            prepare.extract_archive(archive, destination, {"kind": "tar", "archivePrefix": prefix})
    patches = sorted((KIT / "libvlc/patches").glob("*.patch"))
    expected = {p.name: prepare.sha256(p) for p in patches}
    if len(expected) != 47:
        raise ValueError("Expected the exact47-patch upstream VLCKit source snapshot")
    stamp = WORK / "source-patch-state.json"
    if stamp.exists():
        if json.loads(stamp.read_text())["patches"] != expected:
            raise ValueError("Existing source patch state differs; use a fresh work tree")
    else:
        for patch in patches:
            # No index/commit operation; all mutations are confined to this ignored source tree.
            subprocess.run(["git", "apply", "--directory=ThirdParty/work/vlc-79128878", str(patch)], cwd=REPO, check=True)
        stamp.write_text(json.dumps({"count": 47, "patches": expected}, indent=2) + "\n")


def contrib(fetch: bool) -> None:
    config = json.loads((EVIDENCE / "macos-contrib-config.json").read_text())
    build = VLC / "contrib/luma-macos-arm64"
    build.mkdir(parents=True, exist_ok=True)
    environment = os.environ.copy()
    environment.update(config["environment"])
    tools = VLC / "extras/tools/build/bin"
    environment["PATH"] = str(tools) + os.pathsep + environment.get("PATH", "")
    args = ["../bootstrap", "--build=aarch64-apple-darwin", "--host=aarch64-apple-darwin",
            "--prefix=" + str(build / "install")] + config["bootstrapFlags"]
    with (WORK / "contrib-bootstrap.log").open("w") as log:
        subprocess.run(args, cwd=build, env=environment, stdout=log, stderr=subprocess.STDOUT, check=True)
    with (WORK / "contrib-list.log").open("w") as log:
        subprocess.run(["make", "list"], cwd=build, env=environment, stdout=log, stderr=subprocess.STDOUT, check=True)
    if fetch:
        with (WORK / "contrib-fetch.log").open("w") as log:
            subprocess.run(["make", "fetch", "-j4"], cwd=build, env=environment, stdout=log, stderr=subprocess.STDOUT, check=True)


def inventory() -> dict:
    sums = {}
    for path in (VLC / "contrib/src").glob("*/SHA512SUMS"):
        for line in path.read_text(errors="replace").splitlines():
            match = re.match(r"([a-fA-F0-9]{128})\s+\*?(\S+)$", line)
            if match:
                sums.setdefault(match[2], []).append((path.parent.name, match[1]))
    records = []
    for archive in sorted((VLC / "contrib/tarballs").iterdir()):
        if not archive.is_file() or archive.name.startswith("."):
            continue
        digest = hashlib.sha512(archive.read_bytes()).hexdigest()
        matches = [item for item in sums.get(archive.name, []) if item[1] == digest]
        if not matches:
            raise ValueError("Source archive does not match a pinned upstream SHA512: " + archive.name)
        with tarfile.open(archive) as source:
            notices = [name for name in source.getnames() if re.search(
                r"(^|/)(copying[^/]*|licen[cs]e[^/]*|copyright[^/]*|notice[^/]*|authors[^/]*)$", name, re.I)]
        records.append({"filename": archive.name, "bytes": archive.stat().st_size,
                        "sha256": prepare.sha256(archive), "sha512": digest, "verifiedAgainstUpstreamRecipe": True,
                        "recipePackages": [item[0] for item in matches], "licenseNoticePaths": notices})
    if len(records) != 45:
        raise ValueError("Expected45upstream macOS source archives; fetch/check the exact profile")
    result = {"schema": 1, "platform": "macos-arm64", "sourceBase": "79128878", "upstreamPatchSet": "VLCKit319ed2c0:47",
              "bootstrapProfile": "macos-contrib-config.json", "reproductionVerified": False,
              "scope": "Pinned upstream source recipe inputs, not an assertion that the stock framework is LGPL-only.",
              "archives": records, "unmatchedArchives": [], "totalBytes": sum(record["bytes"] for record in records)}
    (EVIDENCE / "macos-source-inventory.json").write_text(json.dumps(result, indent=2) + "\n")
    return result


def bundle(output: Path) -> dict:
    inputs = inventory()
    record = prepare.package("macos-vlckit-3.7.3")
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        raise ValueError("Source bundle already exists; choose a new output path")
    temporary = output.with_suffix(output.suffix + ".partial")
    try:
        with tarfile.open(temporary, "w") as bundle_:
            for source in record["sources"]:
                path = prepare.obtain(source, REPO / "ThirdParty/downloads")
                bundle_.add(path, arcname="upstream/" + path.name)
            for source in inputs["archives"]:
                path = VLC / "contrib/tarballs" / source["filename"]
                bundle_.add(path, arcname="contrib/" + path.name)
            for path in sorted(EVIDENCE.rglob("*")):
                if path.is_file():
                    bundle_.add(path, arcname="audit/" + path.relative_to(EVIDENCE).as_posix())
            for path in sorted((REPO / "Scripts").glob("prepare-libvlc*")):
                bundle_.add(path, arcname="Scripts/" + path.name)
            bundle_.add(REPO / "THIRD_PARTY_NOTICES.md", arcname="THIRD_PARTY_NOTICES.md")
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)
    return {"path": str(output.resolve()), "sha256": prepare.sha256(output), "bytes": output.stat().st_size,
            "upstreamSourceSnapshots": 2, "upstreamPatches": 47, "contribArchives": len(inputs["archives"]),
            "redistributionApproved": False,
            "scope": "Source materials for audit/rebuilding. Stock framework contains GPL modules; no proprietary distribution approval is implied."}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("setup", "fetch-contrib", "inventory", "bundle"))
    parser.add_argument("--output", type=Path, default=REPO / "ThirdParty/downloads/macos-vlckit-3.7.3-source-materials.tar")
    args = parser.parse_args()
    try:
        if args.command == "setup":
            setup()
            result = {"status": "source-snapshots-ready", "patches": 47}
        elif args.command == "fetch-contrib":
            setup()
            contrib(True)
            result = inventory()
        elif args.command == "inventory":
            result = inventory()
        else:
            result = bundle(args.output)
        print(json.dumps(result, indent=2))
        return 0
    except (OSError, ValueError, subprocess.CalledProcessError, tarfile.TarError) as error:
        print(json.dumps({"status": "blocked", "error": str(error)}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
