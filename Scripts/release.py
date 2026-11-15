#!/usr/bin/env python3
"""Review-gated local release preparation. Does not publish or expose private keys."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parent.parent

def run(*args, capture=False):
    result = subprocess.run(list(args), check=True, text=True, capture_output=capture, cwd=ROOT)
    return result.stdout if capture else result

def config_errors(config):
    errors = []
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', config.get('repository', '')):
        errors.append('Set repository to the public releases repository owner/name. Private source may live separately.')
    feed = urlsplit(config.get('feedURL', ''))
    if feed.scheme != 'https' or not feed.hostname or feed.username or feed.password:
        errors.append('Set a credential-free HTTPS feedURL for the signed JSON manifest.')
    try:
        valid_key = len(base64.b64decode(config.get('publicEdKey', ''), validate=True)) == 32
    except ValueError:
        valid_key = False
    if not valid_key:
        errors.append('Generate your own Keychain release key and set publicEdKey to its public key.')
    if config.get('licenseChoice') == 'pending':
        errors.append('Choose the license for original Luma code before distributing it.')
    # Require an explicit production engine policy.
    engine = config.get('engineDistribution')
    if engine == 'libvlc':
        try:
            registry = json.loads((ROOT / 'ThirdParty/libvlc/packages.json').read_text())
            record = registry['packages'][config['libvlcPackage']]
            audit = record['audit']
            if audit.get('status') != 'approved-for-redistribution' or not all(audit.get(key) is True for key in (
                    'moduleLicensesReviewed', 'dependencyLicensesReviewed', 'completeCorrespondingSources', 'replacementMechanismVerified')):
                errors.append('libVLC distribution is blocked by its incomplete module/dependency/source/replacement audit.')
        except (OSError, ValueError, KeyError, TypeError):
            errors.append('Select a registered, audited libVLC package before release.')
    elif engine != 'original-and-system':
        errors.append('Review the playback engine distribution before release.')
    return errors

def notarize(artifact, profile, label):
    result = json.loads(run('xcrun', 'notarytool', 'submit', str(artifact),
        '--keychain-profile', profile, '--wait', '--output-format', 'json', capture=True))
    if result.get('status') != 'Accepted':
        raise RuntimeError('Apple did not accept ' + label + ' notarization. No update manifest was produced.')

def require_reviewed_tree(reviewed_sha):
    if not re.fullmatch(r'[0-9a-f]{40}', reviewed_sha):
        raise RuntimeError('Review the exact full commit SHA before packaging.')
    head = run('git', 'rev-parse', 'HEAD', capture=True).strip()
    if head != reviewed_sha:
        raise RuntimeError('HEAD differs from the reviewed commit.')
    if run('git', 'status', '--porcelain', '--untracked-files=all', capture=True).strip():
        raise RuntimeError('Commit or remove outstanding changes, then review that exact commit.')
    return head

def prepare_dmg(app, destination, identity, profile, bundle_identifier):
    """Package only the reviewed app; temporary submission/staging never ships.

    Apple workflow: https://developer.apple.com/documentation/security/customizing-the-notarization-workflow
    Packaging: https://developer.apple.com/documentation/xcode/packaging-mac-software-for-distribution
    ZIP cannot carry a stapled ticket, so staple the app before copying it into
    the disk image, then independently notarize and staple that signed image.
    """
    image = destination / ('Luma-' + destination.name + '.dmg')
    run('codesign', '--force', '--options', 'runtime', '--timestamp', '--sign', identity, str(app))
    run('codesign', '--verify', '--deep', '--strict', str(app))
    try:
        with tempfile.TemporaryDirectory(prefix='.dmg-work-', dir=destination) as working:
            work = Path(working)
            submission = work / 'app-notarization.zip'
            run('ditto', '-c', '-k', '--sequesterRsrc', '--keepParent', str(app), str(submission))
            notarize(submission, profile, 'app')
            run('xcrun', 'stapler', 'staple', str(app))
            run('xcrun', 'stapler', 'validate', str(app))
            run('codesign', '--verify', '--deep', '--strict', str(app))
            stage = work / 'image'
            stage.mkdir()
            # ditto preserves the app's resource forks, xattrs and stapled ticket.
            run('ditto', str(app), str(stage / app.name))
            (stage / 'Applications').symlink_to('/Applications', target_is_directory=True)
            run('hdiutil', 'create', '-volname', 'Luma Player', '-fs', 'HFS+',
                '-srcfolder', str(stage), '-format', 'UDZO', str(image))
            run('codesign', '--force', '--timestamp', '--identifier', bundle_identifier + '.dmg',
                '--sign', identity, str(image))
            run('codesign', '--verify', '--strict', str(image))
            notarize(image, profile, 'disk image')
            run('xcrun', 'stapler', 'staple', str(image))
            run('xcrun', 'stapler', 'validate', str(image))
            run('codesign', '--verify', '--strict', str(image))
            run('hdiutil', 'verify', str(image))
        return image
    except Exception:
        # A rejected/unfinished DMG must not resemble a ready release artifact.
        image.unlink(missing_ok=True)
        raise

def prepare_preview_dmg(app, destination, bundle_identifier, instructions):
    """Explicit unnotarized preview route, never a production-error fallback.

    Ad-hoc signatures provide local bundle integrity; they do not identify an
    Apple-verified developer or imply notarization. INSTALL.md explains manual
    installation and the per-app Gatekeeper exception to the preview's users.
    """
    image = destination / ('Luma-' + destination.name + '-unnotarized-preview.dmg')
    try:
        run('codesign', '--force', '--sign', '-', str(app))
        run('codesign', '--verify', '--deep', '--strict', str(app))
        with tempfile.TemporaryDirectory(prefix='.dmg-work-', dir=destination) as working:
            stage = Path(working) / 'image'
            stage.mkdir()
            run('ditto', str(app), str(stage / app.name))
            (stage / 'Applications').symlink_to('/Applications', target_is_directory=True)
            shutil.copyfile(instructions, stage / 'INSTALL.md')
            run('hdiutil', 'create', '-volname', 'Luma Player Unnotarized Preview', '-fs', 'HFS+',
                '-srcfolder', str(stage), '-format', 'UDZO', str(image))
            run('codesign', '--force', '--identifier', bundle_identifier + '.preview-dmg', '--sign', '-', str(image))
            run('codesign', '--verify', '--strict', str(image))
            run('hdiutil', 'verify', str(image))
        return image
    except Exception:
        image.unlink(missing_ok=True)
        raise

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    sub.add_parser('doctor')
    sub.add_parser('key')
    release = sub.add_parser('prepare')
    release.add_argument('--reviewed-sha', required=True, help='Exact commit personally reviewed before packaging')
    release.add_argument('--notes', type=Path, required=True)
    release.add_argument('--identity', required=True, help='Your Developer ID Application identity')
    release.add_argument('--notary-profile', required=True, help='An existing notarytool Keychain profile')
    preview = sub.add_parser('prepare-preview', help='Explicitly prepare an ad-hoc signed, unnotarized preview DMG')
    preview.add_argument('--reviewed-sha', required=True, help='Exact commit personally reviewed before preview packaging')
    preview.add_argument('--notes', type=Path, required=True)
    args = parser.parse_args(argv)
    config = json.loads((ROOT / 'ReleaseConfig.json').read_text())
    if args.command == 'doctor':
        errors = config_errors(config)
        for error in errors: print('- ' + error)
        print('Release is blocked.' if errors else 'Public release configuration is complete. Signing/runtime checks run during prepare.')
        return 1 if errors else 0
    signer = ROOT / '.cache/update-signer'
    if args.command == 'key':
        (ROOT / '.cache/modules').mkdir(parents=True, exist_ok=True)
        run('xcrun', 'swiftc', '-module-cache-path', str(ROOT / '.cache/modules'),
            'Sources/SignedRelease.swift', 'Scripts/UpdateSigner.swift', '-o', str(signer))
        run(str(signer), '--generate-key')
        return 0
    errors = config_errors(config)
    if errors: raise RuntimeError('\n'.join(errors))
    head = require_reviewed_tree(args.reviewed_sha)
    is_preview = args.command == 'prepare-preview'
    if not is_preview and 'Developer ID Application:' not in args.identity:
        raise RuntimeError('A production Developer ID Application identity is required.')
    instructions = ROOT / 'INSTALL.md'
    if is_preview and (not instructions.is_file() or not instructions.read_text().strip()):
        raise RuntimeError('Write INSTALL.md with the unnotarized preview and manual-install instructions before packaging.')
    notes_path = args.notes if args.notes.is_absolute() else ROOT / args.notes
    notes = notes_path.read_text()
    (ROOT / '.cache/modules').mkdir(parents=True, exist_ok=True)
    run('xcrun', 'swiftc', '-module-cache-path', str(ROOT / '.cache/modules'),
        'Sources/SignedRelease.swift', 'Scripts/UpdateSigner.swift', '-o', str(signer))
    key = run(str(signer), '--public-key', capture=True).strip()
    if key != config['publicEdKey']: raise RuntimeError('Keychain signing key differs from the app’s pinned public key.')
    if config.get('engineDistribution') == 'libvlc':
        runtime = ROOT / 'ThirdParty/runtime' / config['libvlcPackage']
        verified = json.loads(run('python3', 'Scripts/prepare-libvlc.py', 'verify', '--runtime', str(runtime), '--distribution', capture=True))
        engine_environment = 'LUMA_LIBVLC_PATH=' + verified['runtimeEntry']
        run('env', 'LUMA_DISTRIBUTION_BUILD=1', 'LUMA_LIBVLC_RUNTIME_ROOT=' + str(runtime), 'bash', 'build.sh')
        run('env', engine_environment, 'bash', 'test-vlc.sh')
        run('env', engine_environment, 'bash', 'test-sharing.sh')
    else:
        run('bash', 'build.sh')
    run('bash', 'test.sh')
    run('bash', 'test-updates.sh')
    run('bash', 'test-release-plan.sh')
    run('bash', 'test-codecs.sh')
    run('bash', 'test-msvideo1.sh')
    run('bash', 'test-ms-adpcm.sh')
    run('bash', 'test-pgs.sh')
    run('bash', 'test-pgs-playback.sh')
    run('bash', 'test-native-formats.sh')
    run('bash', 'test-flac.sh')
    run('bash', 'test-jpeg.sh')
    run('bash', 'test-aac.sh')
    run('bash', 'test-avc-timing.sh')
    run('bash', 'test-hevc.sh')
    run('bash', 'test-mkv-audio.sh')
    run('bash', 'test-mkv-text.sh')
    run('bash', 'test-media-language.sh')
    run('bash', 'test-media-clock.sh')
    run('bash', 'test-opus.sh')
    run('bash', 'test-matroska.sh')
    run('bash', 'test-video.sh')
    if config.get('engineDistribution') == 'libvlc':
        run('env', engine_environment, 'bash', 'test-fullscreen.sh')
    else:
        run('bash', 'test-fullscreen.sh')
    app = ROOT / 'build/Luma Player.app'
    info = plistlib.loads((app / 'Contents/Info.plist').read_bytes())
    # The updater remains Luma's own implementation. libVLC inputs must pass the
    # explicit audit gate, rather than being mistaken for an original codec.
    linked = run('otool', '-L', str(app / 'Contents/MacOS/LumaPlayer'), capture=True)
    if 'sparkle' in linked.lower() or (config.get('engineDistribution') != 'libvlc' and (
            'libvlc' in linked.lower() or '/Applications/VLC.app/' in run('otool', '-l', str(app / 'Contents/MacOS/LumaPlayer'), capture=True))):
        raise RuntimeError('The binary still references a removed third-party playback/updater dependency.')
    if config.get('engineDistribution') == 'libvlc':
        run('python3', 'Scripts/prepare-libvlc.py', 'verify', '--runtime', str(app / 'Contents/Resources/libvlc-runtime'), '--distribution')
    version, build = info['CFBundleShortVersionString'], int(info['CFBundleVersion'])
    if not re.fullmatch(r'[0-9]+(?:\.[0-9]+){0,2}', version) or build <= 0:
        raise RuntimeError('A numeric release version and positive build are required.')
    # Verification may take time. Refuse to sign if its reviewed inputs moved.
    require_reviewed_tree(head)
    destination = ROOT / 'build/releases' / str(build)
    destination.mkdir(parents=True, exist_ok=False)
    if is_preview:
        archive = prepare_preview_dmg(app, destination, config['bundleIdentifier'], instructions)
    else:
        archive = prepare_dmg(app, destination, args.identity, args.notary_profile, config['bundleIdentifier'])
    # Hash only after the chosen explicit packaging/signing route completes.
    # For production that includes notarization and staple validation, whose
    # ticket changes the final distribution bytes.
    with archive.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    payload = dict(version=version, build=build,
        url='https://github.com/' + config['repository'] + '/releases/download/v' + version + '/' + archive.name,
        sha256=digest, size=archive.stat().st_size, notes=notes)
    payload_path = destination / 'payload.json'
    manifest = destination / 'manifest.json'
    temporary_manifest = destination / '.manifest.json.pending'
    try:
        payload_path.write_text(json.dumps(payload, separators=(',', ':')))
        run(str(signer), '--sign', str(payload_path), str(temporary_manifest))
        (destination / 'review.json').write_text(json.dumps(dict(reviewed_sha=head, publisher=config['publisher'],
            artifact=archive.name, format='dmg', distribution='unnotarized-preview' if is_preview else 'developer-id-notarized'), indent=2))
        temporary_manifest.replace(manifest)
    except Exception:
        temporary_manifest.unlink(missing_ok=True)
        payload_path.unlink(missing_ok=True)
        archive.unlink(missing_ok=True)
        raise
    print('Prepared locally. No GitHub upload or publication: ' + str(destination))
    return 0

if __name__ == '__main__':
    try: sys.exit(main())
    except (RuntimeError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr); sys.exit(1)
