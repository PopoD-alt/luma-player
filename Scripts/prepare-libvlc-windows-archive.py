#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Original build support; see ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt.
"""Recreate an immutable Windows component archive from inventoried CI files.

This does not approve a runtime. The central preparation/distribution verifier
must subsequently require the independently approved archive and source hashes.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import stat
import tarfile
import tempfile

MANIFEST = 'luma-libvlc-runtime.json'
PACKAGE = 'windows-lgpl-x64-3.0.24-r1'


def regular_tree(root: Path) -> dict:
    result, seen = {}, set()
    pending = [root]
    while pending:
        for path in sorted(pending.pop().iterdir()):
            name = path.relative_to(root).as_posix()
            parts = PurePosixPath(name).parts
            if (not parts or any(p in ('', '.', '..') or ':' in p or '\\' in p for p in parts)
                    or name.casefold() in seen):
                raise ValueError('Unsafe or case-colliding component path: ' + name)
            seen.add(name.casefold())
            info = path.lstat()
            # Reject a junction before enumerating any of its children.
            if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
                raise ValueError('Symlink/reparse point in component: ' + name)
            if stat.S_ISDIR(info.st_mode):
                pending.append(path)
                continue
            if not stat.S_ISREG(info.st_mode):
                raise ValueError('Nonregular component file: ' + name)
            if name != MANIFEST:
                with path.open('rb') as stream:
                    result[name] = {'bytes': info.st_size,
                                    'sha256': hashlib.file_digest(stream, 'sha256').hexdigest()}
    return result


def create(component: Path, output: Path) -> dict:
    if (component.is_symlink() or not component.is_dir()
            or getattr(component.lstat(), 'st_file_attributes', 0) & 0x400):
        raise ValueError('Expected a real component directory')
    component = component.resolve()
    output = output.absolute()
    if output.resolve().is_relative_to(component) or output.exists() or output.is_symlink():
        raise ValueError('Choose a new archive path outside the component')
    files = regular_tree(component)
    manifest = json.loads((component / MANIFEST).read_text(encoding='utf-8-sig'))
    if (manifest.get('schema') != 1 or manifest.get('package') != PACKAGE
            or manifest.get('developmentOnly') is not True
            or manifest.get('redistributionApproved') is not False):
        raise ValueError('Expected the original unapproved Windows CI component')
    if not files or files != manifest.get('files'):
        raise ValueError('Missing, modified or extra component files')
    output.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix='.windows-component-', dir=output.parent)
    try:
        with os.fdopen(descriptor, 'wb') as archive:
            with tarfile.open(fileobj=archive, mode='w', format=tarfile.PAX_FORMAT,
                              encoding='utf-8', errors='strict', pax_headers={}) as tar:
                for name, record in sorted(files.items()):
                    item = tarfile.TarInfo(name)
                    item.size = record['bytes']
                    item.type = tarfile.REGTYPE
                    item.mode = 0o644
                    item.uid = item.gid = item.mtime = 0
                    item.uname = item.gname = ''
                    item.pax_headers = {}
                    with (component / name).open('rb') as source:
                        tar.addfile(item, source)
                        if source.read(1):
                            raise ValueError('Component grew during archival: ' + name)
            archive.flush()
            os.fsync(archive.fileno())
        if regular_tree(component) != files:
            raise ValueError('Component changed during archival')
        # Exclusive creation also preserves an existing destination if another
        # process creates it after the initial check. The partial is never public.
        os.link(temporary, output)
        with output.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        return {'archive': str(output), 'bytes': output.stat().st_size,
                'sha256': digest, 'files': len(files), 'approvalGranted': False}
    finally:
        Path(temporary).unlink(missing_ok=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--component', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(create(args.component, args.output), indent=2))
    except (ValueError, OSError, KeyError, TypeError, json.JSONDecodeError) as error:
        parser.exit(1, str(error) + '\n')


if __name__ == '__main__':
    main()
