#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 sdas
# See ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt.
"""Bundle pinned Android native/binding sources and reviewed build materials.

No stock native AAR, app APK, private signing key, build log or user media is
included. Creating this source companion does not approve a runtime.
"""
from __future__ import annotations
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import sys
import tarfile
import tempfile

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[1]

REBUILD = """# Rebuilding the curated Android arm64 library

This source aggregate preserves upstream licenses, including uncompiled GPL
parts. It is not the stock native AAR and does not license Luma application code
as open source. The original build support is MIT; upstream terms remain intact.

The native source pins are VLC84177b22 plus the20 libvlcjni0b8dc65e patches.
The binding is3.7.7. Two additional recorded patches exclude GPL AArch64
AC3DSP/CRC optimization objects/calls and unused renderer-discovery JNI
initialization. Generic LGPL AC3DSP/CRC and other SIMD remain. The replacement
AAR also omits RendererDiscoverer Java bytecode, all other native ABIs and Lua
assets. These omissions are part of the component's scope.

Requirements: Python3.11+, Git, Make, a C/C++ toolchain, Android SDK and
NDK29.0.14206865, JDK17, and the included host-tool recipes. This build was
performed on an Apple Silicon Mac with Command Line Tools. Install your own SDK
tools under Android/.tools; no private signing key is included. The Android
native recipe does not accept SDK licenses automatically.

Extract this archive into an empty working directory, then run from that root:

```sh
mkdir -p ThirdParty/downloads ThirdParty/libvlc
cp upstream/* ThirdParty/downloads/
cp audit/packages.json ThirdParty/libvlc/
cp -R LumaApplication/Android/. Android/
python3 Android/scripts/build-lgpl-runtime.py prepare
mkdir -p ThirdParty/work/android-lgpl/vlc/contrib/tarballs
cp contrib/* ThirdParty/work/android-lgpl/vlc/contrib/tarballs/
python3 Android/scripts/build-lgpl-runtime.py build
python3 Android/scripts/build-source-aar.py
./Android/gradlew -p Android \\
  -PlumaDevelopmentAar="$PWD/ThirdParty/work/android-lgpl/source-bindings-project/build/outputs/aar/luma-libvlc-source-bindings-release.aar" \\
  assembleDebug
python3 Android/scripts/prepare-native-review.py --help
```

The Git-sourced FluidLite archive has a pinned commit and independently matched
file tree; its recipe can regenerate the local .githash marker by fetching that
commit. The host luac compiler shares the pinned Lua archive. All other selected
dependency inputs are checked against their exact recipe SHA512 values.

The native build recipe recreates the source selection and exclusions. Rebuilds
are not claimed to be byte-identical across SDKs, host toolchains and timestamps.
Review new native/AAR hashes, final source selection and actual Java/JNI playback
before approving a new component revision. Standalone C identity probes do not
initialize the Java VM: engine creation belongs in real JNI/app instrumentation.

For personal library replacement, rebuild compatible libVLC3 libraries and the
filtered AAR, then rebuild/repack and sign a private APK with your own key using
the included helper. Luma's packaging hash gate must not prohibit a user's
compatible modified library. The app's LGPL modification/debugging exception
applies; no general right to redistribute proprietary app derivatives is granted.
The Java wrapper is merged into the app's DEX. The matching application source,
Gradle project and wrapper are therefore included under LumaApplication/ with
the unchanged proprietary license and its LGPL personal-modification exception.
Use those application inputs when relinking a modified Java wrapper; the native
source companion does not relicense Luma's own application code.

Keep this complete source companion and Notices with the review/distribution
materials. Any future APK download must offer the matching complete source from
the same release location or embed it; generating this archive alone does not
prove that public delivery, notarization, patents or legal clearance is complete.
"""


def sha(path: Path) -> str:
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def bundle(work: Path, audit: Path, output: Path) -> dict:
    inventory = json.loads((audit / "source-inventory.json").read_text())
    if inventory.get("issues") or inventory.get("archiveCount") != 56:
        raise ValueError("Expected the completed56-archive Android source inventory")
    registry = json.loads((REPO / "ThirdParty/libvlc/packages.json").read_text())
    inputs = []
    for record in registry["packages"]["android-libvlc-all-3.7.7"]["sources"]:
        path = REPO / "ThirdParty/downloads" / record["filename"]
        if not path.is_file() or sha(path) != record["sha256"]:
            raise ValueError("Pinned upstream source missing or changed: " + record["filename"])
        inputs.append((path, "upstream/" + path.name))
    for record in inventory["archives"]:
        path = work / "vlc/contrib/tarballs" / record["filename"]
        if Path(record["filename"]).name != record["filename"] or sha(path) != record["sha256"]:
            raise ValueError("Verified contributed source changed")
        inputs.append((path, "contrib/" + path.name))
    for name in ("build-lgpl-runtime.py", "filter-native-modules.py", "test-native-filter.py", "prepare-native-review.py", "build-source-aar.py"):
        inputs.append((REPO / "Android/scripts" / name, "Android/scripts/" + name))
    for name in ("ffmpeg-lgpl-aarch64-portable.patch", "jni-no-renderer-discovery.patch"):
        inputs.append((REPO / "Android/scripts/patches" / name, "Android/scripts/patches/" + name))
    for name in ("prepare-libvlc.py", "prepare-libvlc-android-sources.py"):
        inputs.append((REPO / "Scripts" / name, "Scripts/" + name))
    for name in ("source-inventory.json", "elf-evidence.json", "dependency-evidence.json", "aar-evidence.json", "core-jni-source-evidence.json", "playback-evidence.json", "supplemental-notice-inventory.json"):
        inputs.append((audit / name, "audit/" + name))
    for name in ("source-state.json", "build-profile.json", "luma-native-patches.json", "android-module-audit.json", "source-aar-inventory.json"):
        inputs.append((work / name, "audit/" + name))
    inputs += [(REPO / "ThirdParty/libvlc/packages.json", "audit/packages.json"),
               (REPO / "ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt", "Notices/LUMA-BUILD-SUPPORT-LICENSE.txt"),
               (REPO / "THIRD_PARTY_NOTICES.md", "THIRD_PARTY_NOTICES.md")]
    inputs.append((REPO / "LICENSE", "LumaApplication/LICENSE"))
    for name in ("settings.gradle", "build.gradle", "gradle.properties", "gradlew", "gradlew.bat", "app/build.gradle"):
        inputs.append((REPO / "Android" / name, "LumaApplication/Android/" + name))
    for directory in (REPO / "Android/gradle", REPO / "Android/app/src/main"):
        for path in sorted(directory.rglob("*")):
            if path.is_symlink():
                raise ValueError("Application source snapshot must not contain symlinks")
            if path.is_file():
                if path.suffix.lower() in (".mp4", ".mkv", ".mov", ".mp3", ".wav", ".jks", ".keystore"):
                    raise ValueError("Media or signing keys do not belong in the application source snapshot")
                inputs.append((path, "LumaApplication/Android/" + path.relative_to(REPO / "Android").as_posix()))
    for path in sorted((audit / "Notices").rglob("*")):
        if path.is_symlink():
            raise ValueError("Notice symlinks are not allowed")
        if path.is_file():
            inputs.append((path, "Notices/" + path.relative_to(audit / "Notices").as_posix()))
    before = {name: sha(path) for path, name in inputs}
    if len(before) != len(inputs):
        raise ValueError("Duplicate source archive member")
    if output.exists():
        raise ValueError("Source companion already exists; use a fresh output path")
    output.parent.mkdir(parents=True, exist_ok=True)
    descriptor, name = tempfile.mkstemp(prefix=".android-sources-", dir=output.parent)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "wb") as file, tarfile.open(fileobj=file, mode="w") as archive:
            for path, member in inputs:
                archive.add(path, arcname=member, recursive=False)
            text = REBUILD.encode("utf-8")
            info = tarfile.TarInfo("REBUILD.md")
            info.size = len(text)
            archive.addfile(info, io.BytesIO(text))
        if any(sha(path) != before[member] for path, member in inputs):
            raise ValueError("Build/source materials changed during snapshot; repeat after freezing inputs")
        os.link(temporary, output)  # Atomic no-clobber publication of this local source file.
    finally:
        temporary.unlink(missing_ok=True)
    return {"path": str(output.resolve()), "sha256": sha(output), "bytes": output.stat().st_size,
            "contribArchives": len(inventory["archives"]), "upstreamSourceArchives": len(registry["packages"]["android-libvlc-all-3.7.7"]["sources"]),
            "status": "complete-source-materials-not-runtime-approval"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--audit", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(bundle(args.work, args.audit, args.output), indent=2))
        return 0
    except (OSError, ValueError, tarfile.TarError) as error:
        print("Android source companion failed: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
