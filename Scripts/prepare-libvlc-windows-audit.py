#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# See ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt; Luma app code is excluded.
"""Audit the custom Windows component, then stage DLLs plus complete source; never approve it."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import struct
import subprocess
import tarfile
import tempfile

REPO = Path(__file__).resolve().parents[1]
SOURCE_COMMIT = '6de05adcbaf2e8b85fe86aad4169393098628119'
SOURCE_SHA256 = '48e0bf127711d829bcd49d35195b5e1b590f93d1bdd4263266dc4fd367641106'
SOURCE_ARCHIVE = 'vlc-6de05adcba-research.tar.gz'
IMAGE = 'registry.videolan.org/vlc-debian-llvm-ucrt@sha256:a0f9255792ee12da494efebdd203e8d11d769b7d938d5191920f76754d29f6e1'
GPL = re.compile(r'GNU\s+General\s+Public\s+License|VLC_MODULE_LICENSE\s+VLC_LICENSE_GPL|(?<!L)GPL-[23]\.0')
spec = importlib.util.spec_from_file_location('luma_pe', REPO/'Scripts/prepare-libvlc-pe-audit.py')
pe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pe)


def digest(path: Path) -> str:
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def save(root: Path, name: str, data: dict) -> dict:
    (root/name).write_text(json.dumps(data, indent=2)+'\n')
    return data


def command(*args: str, cwd: Path | None = None) -> str:
    return subprocess.check_output(args, cwd=cwd, text=True, stderr=subprocess.PIPE)


def dependencies(build: Path, root: Path, suffixes: set[str]) -> tuple[list[str], set[Path]]:
    build, root = build.resolve(), root.resolve()
    selected = set()
    files = []
    for dep in sorted(build.rglob('*')):
        if dep.suffix not in suffixes:
            continue
        files.append(dep.relative_to(build).as_posix())
        text = dep.read_text(errors='replace').replace('\\\n', ' ')
        for word in shlex.split(text, comments=False):
            if word.endswith(':'):
                continue
            path = (build/word).resolve()
            if path.is_file() and path.is_relative_to(root):
                selected.add(path)
    return files, selected


def gpl_header(path: Path) -> bool:
    text = path.read_text(errors='replace')[:7000]
    opening = re.match(r'\s*/\*.*?\*/', text, re.S)
    text = opening.group() if opening else text
    return bool(GPL.search(re.sub(r'[\s*;]+', ' ', text)))


def audit_ffmpeg(work: Path, source: Path, audit: Path, ar: str) -> dict:
    src = source/'contrib/luma-windows-x64/ffmpeg'
    build = src/'vlc_build'
    depfiles, selected = dependencies(build, src, {'.d'})
    config = (build/'config.h').read_text()
    components = (build/'config_components.h').read_text()
    def define(name, text=config):
        match = re.search(r'^#define '+re.escape(name)+r' (.+)$', text, re.M)
        return match.group(1) if match else None
    settings = {name: define(name) for name in ['FFMPEG_LICENSE', 'CONFIG_GPL', 'CONFIG_GPLV3', 'CONFIG_VERSION3', 'CONFIG_NONFREE']}
    candidates = []
    for directory in ['libavcodec', 'libavformat', 'libavutil', 'libswscale']:
        for path in sorted((src/directory).rglob('*')):
            if path.suffix in {'.c', '.h', '.asm', '.S', '.inc'} and gpl_header(path):
                candidates.append({'path': path.relative_to(src).as_posix(), 'sha256': digest(path), 'selected': path.resolve() in selected})
    archives = []
    for name in ['libavcodec', 'libavformat', 'libavutil', 'libswscale']:
        path = build/name/(name+'.a')
        archives.append({'path': path.relative_to(build).as_posix(), 'sha256': digest(path), 'members': command(ar, 't', str(path)).splitlines()})
    report = save(audit, 'ffmpeg-selection.json', {'scope': 'Actual x64 generated dependencies and built archives, with source/header grant census; no legal clearance',
        'configuration': settings, 'flacEncoder': define('CONFIG_FLAC_ENCODER', components), 'dependencyFiles': depfiles,
        'selectedSourcePaths': [p.relative_to(src).as_posix() for p in sorted(selected)], 'gplHeaderCandidates': candidates, 'archives': archives})
    if not depfiles or not selected or any(row['selected'] for row in candidates):
        raise ValueError('FFmpeg compiled source closure is empty or selects a GPL header candidate')
    if any(settings[n] != '0' for n in ['CONFIG_GPL', 'CONFIG_GPLV3', 'CONFIG_VERSION3', 'CONFIG_NONFREE']) or report['flacEncoder'] != '0':
        raise ValueError('Unexpected FFmpeg GPL/version3/nonfree/encoder configuration')
    if any('flac_dsp_gpl' in member for row in archives for member in row['members']):
        raise ValueError('GPL FLAC assembly object is present in an archive')
    return report


def audit_core(source: Path, build: Path, audit: Path) -> dict:
    paths = set()
    depfiles = []
    for directory in ['src', 'lib', 'compat']:
        files, selected = dependencies(build/directory, source, {'.Plo', '.Po'})
        depfiles.extend(directory+'/'+name for name in files)
        paths.update(selected)
    rows = [{'path': p.relative_to(source).as_posix(), 'sha256': digest(p), 'gplOpeningGrantCandidate': gpl_header(p)}
            for p in sorted(paths) if p.suffix in {'.h', '.c', '.cpp', '.S', '.s', '.asm', '.inc'}]
    report = save(audit, 'core-selection.json', {'scope': 'Actual core/API/compat generated dependencies and opening-comment GPL grant census',
        'dependencyFiles': depfiles, 'sources': rows})
    if not rows or not depfiles or any(row['gplOpeningGrantCandidate'] for row in rows):
        raise ValueError('Core compiled closure is empty or selects a GPL opening grant')
    return report


def source_mapping(build: Path, helper: Path) -> dict:
    helper.write_text('.PHONY: luma-plugin-sources\nluma-plugin-sources:\n\t@$(foreach v,$(filter %_la_SOURCES %_la_LIBADD,$(.VARIABLES)),$(info $(v)=$($(v))))\n')
    values = {}
    for line in command('make', '-f', 'Makefile', '-f', str(helper), 'luma-plugin-sources', cwd=build/'modules').splitlines():
        if line.startswith('lib') and '=' in line:
            name, value = line.split('=', 1)
            values[name] = value.split()
    def expand(name, seen):
        if name in seen:
            return set()
        seen.add(name)
        result = set(values.get(name+'_la_SOURCES', []))
        for entry in values.get(name+'_la_LIBADD', []):
            if entry.endswith('.la'):
                result.update(expand(Path(entry).stem.replace('-', '_'), seen))
        return result
    return {name.removesuffix('_la_SOURCES'): sorted(expand(name.removesuffix('_la_SOURCES'), set()))
            for name in values if name.endswith('_plugin_la_SOURCES')}


def module_sources(source: Path, names: list[str]) -> list[dict]:
    pending = [source/'modules'/name for name in names]
    found = {}
    while pending:
        path = pending.pop().resolve()
        if path in found or not path.is_file() or not path.is_relative_to(source):
            continue
        text = path.read_text(errors='replace')
        header = text[:7000]
        hits = list(GPL.finditer(header))
        normalized = re.sub(r'\s+', ' ', re.sub(r'\n\s*\*\s?', ' ', header))
        clarification = len(hits) == 1 and 'VLC_MODULE_LICENSE' not in hits[0][0] and (
            header[max(0, hits[0].start()-64):hits[0].start()].endswith('You should have received a copy of the ')
            and 'under the terms of the GNU Lesser General Public License as published by' in normalized
            and 'either version 2.1 of the License' in normalized)
        row = {'path': path.relative_to(source).as_posix(), 'sha256': digest(path), 'requiresExclusion': bool(hits) and not clarification}
        if clarification:
            row['clarification'] = 'Explicit LGPL2.1+ grant; the sole GPL mention is an inconsistent final receipt sentence.'
        found[path] = row
        for include in re.findall(r'^\s*#\s*include\s*"([^\"]+)"', text, re.M):
            pending.append(path.parent/include)
    return sorted(found.values(), key=lambda row: row['path'])


def audit_modules(source: Path, build: Path, prefix: Path, audit: Path) -> dict:
    mapping = source_mapping(build, audit/'print-plugin-sources.mk')
    plugins = prefix/'lib/vlc/plugins'
    rows = []
    for path in sorted(plugins.rglob('*.dll')):
        image = pe.Pe(path.read_bytes())
        license_ = image.constant_export(image.exports(), 'vlc_entry_license__')
        names = mapping.get(path.stem, [])
        sources = module_sources(source, names)
        reasons = []
        if path.name == 'libsapi_plugin.dll':
            reasons.append('Optional SAPI speech-synthesis module is excluded from the playback profile')
        if not license_ or 'GNU Lesser General Public License, version 2.1 or later' not in license_:
            reasons.append('Metadata is not inspected LGPL2.1+')
        if not names or not sources:
            reasons.append('Generated source/convenience-library mapping missing')
        if any(row['requiresExclusion'] for row in sources):
            reasons.append('GPL-marked or uncertain source/header')
        rows.append({'path': path.relative_to(plugins).as_posix(), 'sha256': digest(path), 'license': license_,
                     'retained': not reasons, 'reasons': reasons, 'sources': sources})
    report = save(audit, 'modules.json', {'sourceCommit': SOURCE_COMMIT, 'scope': 'Custom x64 PE metadata plus source/convenience-library/quoted-header census',
        'retained': sum(row['retained'] for row in rows), 'excluded': sum(not row['retained'] for row in rows), 'modules': rows})
    required = {'libavcodec_plugin.dll', 'libmkv_plugin.dll', 'libavi_plugin.dll', 'liblibass_plugin.dll', 'libfreetype_plugin.dll', 'libdirect3d11_plugin.dll', 'libwasapi_plugin.dll', 'libpng_plugin.dll'}
    retained = {Path(row['path']).name for row in rows if row['retained']}
    if not rows or required-retained:
        raise ValueError('Required playback modules not retained: '+str(sorted(required-retained)))
    merged = next((row for row in rows if Path(row['path']).name == 'libavcodec_plugin.dll' and row['retained']), None)
    if merged is None or not any(row['path'] == 'modules/demux/avformat/demux.c' for row in merged['sources']):
        raise ValueError('Merged FFmpeg plugin has no audited avformat demux source closure')
    return report


def imports(path: Path) -> list[str]:
    image = pe.Pe(path.read_bytes())
    optional = image.u32(0x3c)+24
    rva, _ = struct.unpack('<II', image.bytes(optional+120, 8))
    result = []
    if rva:
        start = image.offset(rva, 20)
        for index in range(256):
            row = struct.unpack('<IIIII', image.bytes(start+index*20, 20))
            if not any(row):
                return result
            result.append(image.string(row[3]))
        raise ValueError('PE import limit exceeded')
    return result


def constant_getter(path: Path, name: str) -> str:
    image = pe.Pe(path.read_bytes())
    rva = image.exports()[name]
    code = image.bytes(image.offset(rva, 8), 8)
    if code[:3] != b'\x48\x8d\x05' or code[7] != 0xc3:
        raise ValueError('Unrecognized constant PE getter: '+name)
    return image.string(rva+7+struct.unpack_from('<i', code, 3)[0])


def audit_imports(prefix: Path, modules: dict, audit: Path, expected: str) -> dict:
    paths = [prefix/'bin/libvlc.dll', prefix/'bin/libvlccore.dll']
    paths.extend(prefix/'lib/vlc/plugins'/row['path'] for row in modules['modules'] if row['retained'])
    allowed = {'advapi32.dll', 'crypt32.dll', 'dsound.dll', 'gdi32.dll', 'iphlpapi.dll', 'kernel32.dll', 'mfplat.dll', 'mpr.dll', 'netapi32.dll',
               'shell32.dll', 'shlwapi.dll', 'user32.dll', 'usp10.dll', 'winmm.dll', 'ws2_32.dll', 'xmllite.dll', 'bcrypt.dll', 'ole32.dll', 'libvlccore.dll'}
    rows = []
    for path in paths:
        names = imports(path)
        unknown = [n for n in names if not re.fullmatch(r'[A-Za-z0-9_.-]+\.dll', n, re.I) or (n.lower() not in allowed and not re.fullmatch(r'(?:api|ext)-ms-win-[a-z0-9-]+\.dll', n, re.I))]
        if unknown:
            raise ValueError('Unreviewed static PE dependency in '+path.name+': '+str(unknown))
        rows.append({'path': path.relative_to(prefix).as_posix(), 'sha256': digest(path), 'imports': names})
    revision = constant_getter(prefix/'bin/libvlc.dll', 'libvlc_get_changeset')
    version = constant_getter(prefix/'bin/libvlc.dll', 'libvlc_get_version')
    if revision != expected or version != '3.0.24 Vetinari':
        raise ValueError('Runtime version/revision mismatch; possible enclosing Git contamination')
    return save(audit, 'pe-imports.json', {'scope': 'All retained static PE imports; runtime LoadLibrary and Windows playback need native testing',
        'version': version, 'changeset': revision, 'binaries': rows})


def validate_destination(output: Path, inputs: list[Path]) -> Path:
    output = output.resolve()
    if output.exists() or any(output.is_relative_to(root.resolve()) for root in inputs):
        raise ValueError('Choose a new component destination outside every input tree')
    return output


def stage(work: Path, source: Path, prefix: Path, audit: Path, output: Path, modules: dict, strip: str) -> dict:
    output = validate_destination(output, [source, prefix, audit, work/'source-evidence'])
    evidence = work/'source-evidence'
    inventory = json.loads((evidence/'source-inventory.json').read_text())
    if inventory.get('issues') != [] or not inventory.get('archives'):
        raise ValueError('Source/notice inventory is incomplete')
    archive = work/'downloads'/SOURCE_ARCHIVE
    if archive.is_symlink() or digest(archive) != SOURCE_SHA256:
        raise ValueError('Exact source snapshot SHA mismatch')
    output.parent.mkdir(parents=True, exist_ok=True)
    temp = Path(tempfile.mkdtemp(prefix='.windows-lgpl-', dir=output.parent))
    try:
        for name in ['libvlc.dll', 'libvlccore.dll']:
            shutil.copyfile(prefix/'bin'/name, temp/name)
        for row in modules['modules']:
            if row['retained']:
                target = temp/'plugins'/row['path']
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(prefix/'lib/vlc/plugins'/row['path'], target)
        for path in temp.rglob('*.dll'):
            subprocess.run([strip, '--strip-debug', str(path)], check=True, capture_output=True)
            pe.Pe(path.read_bytes())
        shutil.copytree(evidence/'Notices', temp/'Notices')
        shutil.copyfile(REPO/'ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt', temp/'Notices/LUMA-BUILD-SUPPORT-LICENSE.txt')
        shutil.copytree(audit, temp/'Audit', ignore=shutil.ignore_patterns('Notices'))
        shutil.copyfile(evidence/'source-inventory.json', temp/'Audit/source-inventory.json')
        (temp/'Sources').mkdir()
        bundle = temp/'Sources/CorrespondingSource.tar'
        with tarfile.open(bundle, 'w') as tar:
            tar.add(archive, arcname='upstream/'+archive.name)
            for row in inventory['archives']:
                path = source/'contrib/tarballs'/row['filename']
                if path.is_symlink() or digest(path) != row['sha256']:
                    raise ValueError('Changed contributed archive: '+row['filename'])
                tar.add(path, arcname='contrib/'+path.name)
            for path in sorted((REPO/'Scripts').glob('prepare-libvlc*')):
                if path.is_file():
                    tar.add(path, arcname='Scripts/'+path.name)
            tar.add(REPO/'ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt', arcname='ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt')
            tar.add(audit, arcname='audit')
            tar.add(evidence, arcname='audit/source-evidence')
        files = {p.relative_to(temp).as_posix(): {'sha256': digest(p), 'bytes': p.stat().st_size} for p in sorted(temp.rglob('*')) if p.is_file()}
        manifest = {'schema': 1, 'package': 'windows-lgpl-x64-3.0.24-r1', 'developmentOnly': True, 'redistributionApproved': False,
                    'auditStatus': 'technical-profile-passed-awaiting-native-and-independent-review', 'sourceCommit': SOURCE_COMMIT, 'buildImage': IMAGE,
                    'retainedPlugins': modules['retained'], 'excludedPlugins': modules['excluded'], 'files': files,
                    'notice': 'Only the curated native component and complete source/notices. No Luma executable or installer. No legal/patent clearance or physical-audio claim.'}
        (temp/'luma-libvlc-runtime.json').write_text(json.dumps(manifest, indent=2)+'\n')
        if list(temp.rglob('*.exe')) or list(temp.rglob('plugins.dat')):
            raise ValueError('Unexpected executable or unfiltered cache in staged component')
        os.replace(temp, output)
        return {'component': str(output), 'sourceBundleSha256': digest(output/'Sources/CorrespondingSource.tar'), 'files': len(files),
                'retainedPlugins': modules['retained'], 'redistributionApproved': False}
    finally:
        if temp.exists():
            shutil.rmtree(temp)



def snapshot_configuration(source: Path, build: Path, audit: Path) -> None:
    entries = {
        'revision.txt': source/'src/revision.txt',
        'vlc-config.h': build/'config.h',
        'contrib-Makefile': source/'contrib/luma-windows-x64/Makefile',
        'contrib-config.mak': source/'contrib/luma-windows-x64/config.mak',
        'ffmpeg-config.h': source/'contrib/luma-windows-x64/ffmpeg/vlc_build/config.h',
        'ffmpeg-config_components.h': source/'contrib/luma-windows-x64/ffmpeg/vlc_build/config_components.h',
        'ffmpeg-config.asm': source/'contrib/luma-windows-x64/ffmpeg/vlc_build/config.asm',
        'ffmpeg-config.mak': source/'contrib/luma-windows-x64/ffmpeg/vlc_build/ffbuild/config.mak',
    }
    destination = audit/'BuildInputs'
    destination.mkdir(exist_ok=True)
    records = {}
    for name, path in entries.items():
        if not path.is_file():
            raise ValueError('Missing actual build configuration: '+name)
        shutil.copyfile(path, destination/name)
        records[name] = {'sourcePath': path.relative_to(source).as_posix(), 'sha256': digest(path)}
    save(destination, 'inventory.json', records)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--stage', type=Path)
    parser.add_argument('--expected-revision', default='Luma-LGPL-Windows-6de05adcba-r1')
    parser.add_argument('--ar', default='x86_64-w64-mingw32-ar')
    parser.add_argument('--strip', default='x86_64-w64-mingw32-strip')
    args = parser.parse_args()
    work = args.work.resolve()
    source = work/'vlc-6de05adcba'
    build = source/'build-luma-windows'
    prefix = work/'unfiltered-install'
    audit = work/'audit'
    audit.mkdir(exist_ok=True)
    try:
        snapshot_configuration(source, build, audit)
        audit_ffmpeg(work, source, audit, args.ar)
        audit_core(source, build, audit)
        modules = audit_modules(source, build, prefix, audit)
        audit_imports(prefix, modules, audit, args.expected_revision)
        result = {'technicalChecks': 'passed', 'retained': modules['retained'], 'excluded': modules['excluded'], 'nativeWindowsVerified': False}
        if args.stage:
            result.update(stage(work, source, prefix, audit, args.stage, modules, args.strip))
        print(json.dumps(result, indent=2))
        return 0
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print('Windows runtime audit failed: '+str(error), file=os.sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
