#!/usr/bin/env python3
"""Curate the locally built arm64 libVLC; never relabel an upstream stock binary.

This is a technical inventory, not legal clearance. Source/header checks augment
the module metadata, whose upstream default alone is insufficient evidence.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

REPO = Path(__file__).resolve().parents[1]
VLC = REPO / "ThirdParty/work/vlc-79128878"
BUILD = VLC / "build-luma-lgpl-arm64"
PREFIX = REPO / "ThirdParty/work/lgpl-macos-arm64-install"
AUDIT = REPO / "ThirdParty/libvlc/macos-custom-module-audit.json"
GPL = re.compile(r"GNU\s+General\s+Public\s+License|VLC_MODULE_LICENSE\s+VLC_LICENSE_GPL")


def digest(path: Path) -> str:
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def run(*args: str, cwd: Path | None = None) -> str:
    return subprocess.check_output(args, cwd=cwd, text=True, stderr=subprocess.PIPE)


def source_lists() -> dict[str, list[str]]:
    helper = REPO / "ThirdParty/work/print-plugin-sources.mk"
    helper.write_text(".PHONY: luma-plugin-sources\nluma-plugin-sources:\n"
                      "\t@$(foreach v,$(filter %_la_SOURCES %_la_LIBADD,$(.VARIABLES)),$(info $(v)=$($(v))))\n")
    output = run("make", "-f", "Makefile", "-f", str(helper), "luma-plugin-sources", cwd=BUILD / "modules")
    variables = {}
    for line in output.splitlines():
        if line.startswith("lib") and "=" in line:
            name, value = line.split("=", 1)
            variables[name] = value.split()

    def expand(name: str, visited: set[str]) -> set[str]:
        if name in visited:
            return set()
        visited.add(name)
        sources = set(variables.get(name + "_la_SOURCES", []))
        # Convenience libraries contain implementation files which entry-source
        # includes cannot reveal (for example adaptive/http demux helpers).
        for dependency in variables.get(name + "_la_LIBADD", []):
            if dependency.endswith(".la"):
                library = Path(dependency).stem.replace("-", "_")
                sources.update(expand(library, visited))
        return sources

    return {name.removesuffix("_la_SOURCES"): sorted(expand(name.removesuffix("_la_SOURCES"), set()))
            for name in variables if name.endswith("_plugin_la_SOURCES")}


def inspect_sources(names: list[str]) -> list[dict]:
    root = VLC.resolve()
    pending = [root / "modules" / name for name in names]
    found = {}
    while pending:
        path = pending.pop().resolve()
        if path in found or not path.is_relative_to(root) or not path.is_file():
            continue
        text = path.read_text(errors="replace")
        name = path.relative_to(root).as_posix()
        header = text[:7000]
        matches = list(GPL.finditer(header))
        # Some TS helpers expressly grant LGPL2.1+ yet retain a final GPL
        # receipt sentence. That notification is not their operative grant.
        # Recognize only this narrow, unambiguous upstream boilerplate pattern.
        normalized = re.sub(r"\s+", " ", re.sub(r"\n\s*\*\s?", " ", header))
        clarification = len(matches) == 1 and "VLC_MODULE_LICENSE" not in matches[0][0] and (
            header[max(0, matches[0].start() - 64):matches[0].start()].endswith("You should have received a copy of the ")
            and "under the terms of the GNU Lesser General Public License as published by" in normalized
            and "either version 2.1 of the License" in normalized)
        found[path] = {"path": name, "sha256": digest(path), "gplNotice": bool(matches),
                       "requiresExclusion": bool(matches) and not clarification}
        if clarification:
            found[path]["licenseClarification"] = "Explicit LGPL2.1+ grant; sole GPL mention is the inconsistent final receipt sentence, not a GPL grant."
        # Conservatively inspect even conditionally included quoted headers.
        for include in re.findall(r'^\s*#\s*include\s*"([^\"]+)"', text, re.M):
            pending.append(path.parent / include)
    return sorted(found.values(), key=lambda item: item["path"])


def inspect() -> dict:
    core = ctypes.CDLL(str(PREFIX / "lib/libvlccore.9.dylib"), mode=ctypes.RTLD_GLOBAL)
    mapping = source_lists()
    records = []
    plugins = PREFIX / "lib/vlc/plugins"
    for path in sorted(plugins.rglob("*.dylib")):
        reason = []
        license_ = "unreadable"
        try:
            library = ctypes.CDLL(str(path))
            getter = getattr(library, "vlc_entry_license__3_0_0f")
            getter.restype = ctypes.c_char_p
            license_ = getter().decode("utf-8")
        except (OSError, AttributeError, UnicodeError):
            reason.append("Native license metadata unavailable")
        if "GNU Lesser General Public License, version 2.1 or later" not in license_:
            reason.append("Not an inspected LGPL2.1+ module")
        names = mapping.get(path.stem, [])
        sources = inspect_sources(names)
        if not names or not sources:
            reason.append("Generated source mapping missing")
        gpl = [source["path"] for source in sources if source["requiresExclusion"]]
        if gpl:
            reason.append("Conservatively excluded GPL-marked source/header")
        records.append({"path": path.relative_to(plugins).as_posix(), "binarySha256": digest(path), "license": license_,
                        "retained": not reason, "reasons": reason, "gplSourceHits": gpl,
                        "sources": sources})
    if not records:
        raise ValueError("No generated plugins")
    # Keep the core loaded while reading the plugin constant-string exports.
    assert core is not None
    return {"schema": 1, "scope": "Original custom dynamic build; not the stock VLCKit binary",
            "sourceBase": "79128878", "patchSet": "VLCKit319ed2c0 macOS47patches",
            "selection": "LGPL2.1+ metadata AND expanded source/quoted-header inventory without GPL restriction; uncertain modules excluded. Explicit LGPL2.1+ grants with only a stale GPL receipt sentence are individually recorded.",
            "limits": "Not legal clearance; conditionals are treated conservatively. Contrib and core require their separate build/source audit.",
            "retained": sum(item["retained"] for item in records),
            "excluded": sum(not item["retained"] for item in records), "modules": records}


def validate_macho(path: Path) -> dict:
    if run("lipo", "-archs", str(path)).split() != ["arm64"]:
        raise ValueError("Expected an arm64-only audited binary: " + path.name)
    load = run("otool", "-l", str(path))
    versions = re.findall(r"\bminos\s+([0-9.]+)", load)
    versions += re.findall(r"cmd LC_VERSION_MIN_MACOSX\s+cmdsize \d+\s+version ([0-9.]+)", load)
    if not versions or any(tuple(map(int, value.split("."))) > (14, 0, 0) for value in versions):
        raise ValueError("Deployment target is missing or newer than macOS14: " + path.name + " " + str(versions))
    dependencies = [line.strip().split(" (", 1)[0] for line in run("otool", "-L", str(path)).splitlines()[2:]]
    return {"minimumOS": versions, "dependencies": dependencies}


def stage(destination: Path, report: dict, source_bundle: Path) -> dict:
    if destination.exists():
        raise ValueError("Refusing to overwrite runtime; choose a new --stage path")
    if not source_bundle.is_file():
        raise ValueError("Complete corresponding-source bundle is required")
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix=".lgpl-curate-", dir=destination.parent))
    try:
        library = temporary / "lib"
        library.mkdir()
        for name in ("libvlc.5.dylib", "libvlccore.9.dylib"):
            shutil.copyfile(PREFIX / "lib" / name, library / name)
        # Preserve the public ABI aliases for user replacement and external tools.
        (library / "libvlc.dylib").symlink_to("libvlc.5.dylib")
        (library / "libvlccore.dylib").symlink_to("libvlccore.9.dylib")
        for module in report["modules"]:
            if module["retained"]:
                target = library / "vlc/plugins" / module["path"]
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(PREFIX / "lib/vlc/plugins" / module["path"], target)
        binary_inventory = {}
        for path in sorted(library.rglob("*.dylib")):
            if path.is_symlink():
                continue
            data = validate_macho(path)
            subprocess.run(["install_name_tool", "-id", "@rpath/" + path.name, str(path)], check=True)
            for dependency in data["dependencies"]:
                if dependency.startswith(("/usr/lib/", "/System/Library/")):
                    continue
                if Path(dependency).name not in ("libvlc.dylib", "libvlc.5.dylib", "libvlccore.dylib", "libvlccore.9.dylib"):
                    raise ValueError("Unreviewed dynamic dependency: " + dependency)
                target = library / ("libvlccore.9.dylib" if "libvlccore" in dependency else "libvlc.5.dylib")
                relative = os.path.relpath(target, path.parent)
                subprocess.run(["install_name_tool", "-change", dependency, "@loader_path/" + relative, str(path)], check=True)
            subprocess.run(["codesign", "--force", "--sign", "-", str(path)], check=True, capture_output=True)
            binary_inventory[path.relative_to(temporary).as_posix()] = {**validate_macho(path), "sha256": digest(path)}
        notices = temporary / "Notices"
        shutil.copytree(REPO / "ThirdParty/libvlc/Notices", notices)
        for name in ("COPYING.LIB", "COPYING.GPL-2.0", "LUMA-BUILD-SUPPORT-LICENSE.txt"):
            shutil.copyfile(REPO / "ThirdParty/libvlc" / name, notices / name)
        shutil.copyfile(REPO / "THIRD_PARTY_NOTICES.md", notices / "Luma-THIRD_PARTY_NOTICES.md")
        for name in ("AUTHORS", "THANKS"):
            shutil.copyfile(VLC / name, notices / ("VLC-" + name))
        sources = temporary / "Sources"
        sources.mkdir()
        shutil.copyfile(source_bundle, sources / "CorrespondingSource.tar")
        provenance = {"schema": 1, "platform": "macOS14+-arm64", "libraryVersion": "3.0.23",
                      "sourceBundle": {"path": "Sources/CorrespondingSource.tar", "sha256": digest(source_bundle)},
                      "buildDriver": "Scripts/prepare-libvlc-macos.sh",
                      "buildSequence": ["tools", "contrib-reset", "core-reset"],
                      "contribLicenseFlags": ["--disable-gpl", "--disable-gnuv3", "--enable-ad-clauses"],
                      "moduleAudit": report, "binaries": binary_inventory,
                      "notice": "Original custom build with conservative plugin exclusions. No patent or legal clearance is claimed."}
        (temporary / "build-provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
        os.replace(temporary, destination)
        return {"runtimeRoot": str(destination.resolve()), "runtimeEntry": str((destination / "lib/libvlc.5.dylib").resolve()),
                "retainedPlugins": report["retained"], "excludedPlugins": report["excluded"],
                "sourceBundleSha256": provenance["sourceBundle"]["sha256"],
                "status": "curated; distribution registry approval still required"}
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", type=Path)
    parser.add_argument("--source-bundle", type=Path)
    parser.add_argument("--create-source-bundle", type=Path,
                        help="Create the complete source snapshot after the final module audit, before staging")
    arguments = parser.parse_args()
    try:
        report = inspect()
        AUDIT.write_text(json.dumps(report, indent=2) + "\n")
        source_bundle = arguments.source_bundle
        if arguments.create_source_bundle:
            if source_bundle:
                parser.error("Choose --source-bundle or --create-source-bundle")
            source_bundle = arguments.create_source_bundle
            subprocess.run([sys.executable, str(REPO / "Scripts/prepare-libvlc-sources.py"),
                            "bundle", "--output", str(source_bundle)], check=True, stdout=subprocess.PIPE)
        if arguments.stage:
            if not source_bundle:
                parser.error("--stage requires --source-bundle or --create-source-bundle")
            print(json.dumps(stage(arguments.stage, report, source_bundle), indent=2))
        else:
            print(json.dumps({"retained": report["retained"], "excluded": report["excluded"], "audit": str(AUDIT)}))
        return 0
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        print("libVLC curation failed: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
