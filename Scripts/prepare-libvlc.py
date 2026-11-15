#!/usr/bin/env python3
"""Pinned libVLC acquisition and a fail-closed redistribution gate (standard library only)."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import sys
import tarfile
import tempfile
import urllib.request
import zipfile

REPO = Path(__file__).resolve().parents[1]
REGISTRY = REPO / "ThirdParty/libvlc/packages.json"
MANIFEST = "luma-libvlc-runtime.json"
ALLOWED_HOSTS = {"download.videolan.org", "api.nuget.org", "repo.maven.apache.org", "codeload.github.com"}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def registry() -> dict:
    return json.loads(REGISTRY.read_text(encoding="utf-8"))


def package(identifier: str) -> dict:
    try:
        return registry()["packages"][identifier]
    except KeyError as error:
        raise ValueError("Unknown pinned package: " + identifier) from error


def within(root: Path, value: Path) -> bool:
    return value.resolve().is_relative_to(root.resolve())


def relative(name: str) -> PurePosixPath:
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts or "\\" in name or not path.parts:
        raise ValueError("Unsafe archive/runtime path: " + name)
    return path


def obtain(record: dict, cache: Path) -> Path:
    """A local hash is pinned evidence, not a substitute for a publisher signature."""
    from urllib.parse import urlparse
    url = record["url"]
    parsed = urlparse(url)
    if parsed.scheme != "https" or parsed.hostname not in ALLOWED_HOSTS or parsed.username or parsed.password:
        raise ValueError("Unapproved download URL")
    cache.mkdir(parents=True, exist_ok=True)
    target = cache / record["filename"]
    if target.name != record["filename"]:
        raise ValueError("Invalid cache filename")
    if target.exists():
        if sha256(target) != record["sha256"]:
            raise ValueError("Cached artifact hash differs: " + target.name)
        return target
    temporary = target.with_suffix(target.suffix + ".partial")
    try:
        with urllib.request.urlopen(url, timeout=60) as response, temporary.open("xb") as output:
            if urlparse(response.url).scheme != "https":
                raise ValueError("Download redirected away from HTTPS")
            count = 0
            while block := response.read(1024 * 1024):
                count += len(block)
                if count > record.get("maximumBytes", 1024 * 1024 * 1024):
                    raise ValueError("Artifact exceeds the download bound")
                output.write(block)
        if sha256(temporary) != record["sha256"]:
            raise ValueError("Downloaded artifact hash differs: " + target.name)
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)
    return target


def extract_archive(archive: Path, destination: Path, record: dict) -> None:
    """Extract only a pinned subtree; retain internal framework symlinks, reject escapes."""
    prefix = record.get("archivePrefix", "")
    if record["kind"] == "aar":
        shutil.copyfile(archive, destination / archive.name)
        return
    maximum = 1024 * 1024 * 1024
    total = 0
    if record["kind"] == "zip":
        with zipfile.ZipFile(archive) as source:
            for member in source.infolist():
                if not member.filename.startswith(prefix):
                    continue
                name = member.filename[len(prefix):]
                if not name:
                    continue
                path = destination.joinpath(*relative(name).parts)
                total += member.file_size
                if total > maximum or (member.external_attr >> 16) & 0o170000 == 0o120000:
                    raise ValueError("Oversized archive or unsupported ZIP symlink")
                if member.is_dir():
                    path.mkdir(parents=True, exist_ok=True)
                else:
                    path.parent.mkdir(parents=True, exist_ok=True)
                    with source.open(member) as input_, path.open("xb") as output:
                        shutil.copyfileobj(input_, output)
    elif record["kind"] == "tar":
        links = []
        with tarfile.open(archive) as source:
            for member in source:
                if not member.name.startswith(prefix):
                    continue
                name = member.name[len(prefix):].lstrip("/")
                if not name:
                    continue
                path = destination.joinpath(*relative(name).parts)
                total += member.size
                if total > maximum:
                    raise ValueError("Archive exceeds extraction bound")
                if member.isdir():
                    path.mkdir(parents=True, exist_ok=True)
                elif member.isfile():
                    path.parent.mkdir(parents=True, exist_ok=True)
                    with source.extractfile(member) as input_, path.open("xb") as output:
                        shutil.copyfileobj(input_, output)
                    path.chmod(member.mode & 0o777)
                elif member.issym():
                    target = path.parent / member.linkname
                    if Path(member.linkname).is_absolute() or not within(destination, target):
                        raise ValueError("Escaping archive symlink")
                    links.append((path, member.linkname))
                else:
                    raise ValueError("Unsupported archive entry")
        for path, link in links:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.symlink_to(link)
    else:
        raise ValueError("Unsupported archive kind")


def inventory(root: Path) -> dict:
    result = {}
    for path in sorted(root.rglob("*")):
        name = path.relative_to(root).as_posix()
        if name == MANIFEST:
            continue
        if not within(root, path):
            raise ValueError("Runtime symlink escapes its root")
        if path.is_symlink():
            result[name] = {"symlink": os.readlink(path)}
        elif path.is_file():
            result[name] = {"sha256": sha256(path), "bytes": path.stat().st_size}
    return result


def distribution_check(record: dict) -> None:
    audit = record["audit"]
    if audit.get("status") != "approved-for-redistribution" or not all(audit.get(key) is True for key in (
        "moduleLicensesReviewed", "dependencyLicensesReviewed", "completeCorrespondingSources", "replacementMechanismVerified")):
        raise ValueError("Redistribution blocked: " + "; ".join(audit.get("blockers", ["No complete approved audit"])))


def verify(runtime: Path, distribution: bool = False) -> dict:
    manifest = json.loads((runtime / MANIFEST).read_text(encoding="utf-8"))
    record = package(manifest["package"])
    if distribution:
        distribution_check(record)
        if manifest.get("developmentOnly") is not False:
            raise ValueError("A development runtime cannot enter a distributable artifact")
    if manifest.get("artifactSha256") != record["artifact"]["sha256"]:
        raise ValueError("Runtime does not match its registered artifact")
    if inventory(runtime) != manifest.get("files"):
        raise ValueError("Runtime inventory differs: missing, changed or unreviewed files")
    entry = runtime / record["runtimeEntry"]
    if not entry.is_file() or not within(runtime, entry):
        raise ValueError("Runtime entry is missing or outside its root")
    if record.get("runtimeSha256") and sha256(entry) != record["runtimeSha256"]:
        raise ValueError("Runtime entry differs from inspected upstream bytes")
    return {"package": manifest["package"], "runtimeRoot": str(runtime.resolve()), "runtimeEntry": str(entry.resolve()),
            "manifestPath": str((runtime / MANIFEST).resolve()), "developmentOnly": manifest["developmentOnly"],
            "redistributionApproved": not manifest["developmentOnly"], "status": "verified"}


def prepare(identifier: str, destination: Path, development: bool) -> dict:
    record = package(identifier)
    if not development:
        distribution_check(record)
    if destination.exists():
        result = verify(destination, not development)
        if result["package"] != identifier:
            raise ValueError("Destination belongs to another package")
        return result
    archive = obtain(record["artifact"], REPO / "ThirdParty/downloads")
    destination.parent.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix=".libvlc-stage-", dir=destination.parent))
    try:
        extract_archive(archive, stage, record)
        notices = stage / "Notices"
        notices.mkdir(exist_ok=True)
        for filename in ("COPYING.LIB", "COPYING.GPL-2.0"):
            shutil.copyfile(REPO / "ThirdParty/libvlc" / filename, notices / filename)
        shutil.copyfile(REPO / "THIRD_PARTY_NOTICES.md", notices / "Luma-THIRD_PARTY_NOTICES.md")
        manifest = {"schema": 1, "package": identifier, "artifactSha256": record["artifact"]["sha256"],
                    "developmentOnly": development, "redistributionApproved": not development,
                    "auditStatus": record["audit"]["status"], "files": inventory(stage)}
        (stage / MANIFEST).write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        verify(stage, not development)
        os.replace(stage, destination)
        return verify(destination, not development)
    finally:
        if stage.exists():
            shutil.rmtree(stage)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("status")
    prep = commands.add_parser("prepare")
    prep.add_argument("--package", required=True)
    prep.add_argument("--runtime", type=Path)
    mode = prep.add_mutually_exclusive_group(required=True)
    mode.add_argument("--development", action="store_true")
    mode.add_argument("--distribution", action="store_true")
    check = commands.add_parser("verify")
    check.add_argument("--runtime", type=Path, required=True)
    check.add_argument("--distribution", action="store_true")
    sources = commands.add_parser("fetch-sources")
    sources.add_argument("--package", required=True)
    args = parser.parse_args()
    try:
        if args.command == "status":
            output = registry()
        elif args.command == "prepare":
            output = prepare(args.package, args.runtime or REPO / "ThirdParty/runtime" / args.package, args.development)
        elif args.command == "verify":
            output = verify(args.runtime, args.distribution)
        else:
            record = package(args.package)
            output = {"package": args.package, "completeCorrespondingSources": record["audit"]["completeCorrespondingSources"],
                      "archives": [{"path": str(obtain(source, REPO / "ThirdParty/downloads")), "sha256": source["sha256"]} for source in record["sources"]]}
        print(json.dumps(output, indent=2))
        return 0
    except (OSError, ValueError, KeyError, tarfile.TarError, zipfile.BadZipFile) as error:
        print(json.dumps({"status": "blocked", "error": str(error)}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
