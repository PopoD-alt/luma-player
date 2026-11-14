#!/usr/bin/env python3
"""Exercise release orchestration using synthetic files and stubbed commands.

No real compiler, app build, hdiutil, signing, Keychain, notarization, network,
or publishing command is executed. The .dmg bytes here are deliberately not a
disk image; they only test that final stapled bytes reach the signed manifest.
"""
import base64
import hashlib
import importlib.util
import json
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('luma_release_plan', Path(__file__).resolve().parents[1] / 'Scripts/release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleasePlanSmoke(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='luma-mocked-release-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.app = self.root / 'build/Luma Player.app'
        (self.app / 'Contents/MacOS').mkdir(parents=True)
        (self.app / 'Contents/MacOS/LumaPlayer').write_bytes(b'not an executable')
        (self.app / 'Contents/Info.plist').write_bytes(plistlib.dumps({
            'CFBundleShortVersionString': '0.6.0', 'CFBundleVersion': '7'}))
        self.config = dict(repository='example/luma-releases', feedURL='https://example.invalid/manifest.json',
            publicEdKey=base64.b64encode(bytes(32)).decode(), licenseChoice='private-source',
            engineDistribution='original-and-system', bundleIdentifier='dev.example.luma', publisher='test')
        (self.root / 'ReleaseConfig.json').write_text(json.dumps(self.config))
        (self.root / 'RELEASE_NOTES.md').write_text('Synthetic release plan test.\n')
        self.instructions = '# Unnotarized alpha preview\n\nDrag Luma Player.app to Applications. Quit the old app before replacing it.\nIf blocked, use Privacy & Security → Open Anyway for this app only.\n'
        (self.root / 'INSTALL.md').write_text(self.instructions)
        self.sha = 'a' * 40
        self.head = self.sha
        self.dirty = ''
        self.key = self.config['publicEdKey']
        self.calls = []
        self.statuses = ['Accepted', 'Accepted']
        self.failure = None
        self.notary_count = 0
        self.stage_checked = False
        self.signer_failure = False
        self.change_after_checks = None
        self.is_preview = False
        self.destination = self.root / 'build/releases/7'
        self.image = self.destination / 'Luma-7.dmg'
        self.manifest = self.destination / 'manifest.json'
        self.addCleanup(mock.patch.stopall)
        mock.patch.object(release, 'ROOT', self.root).start()
        mock.patch.object(release, 'run', side_effect=self.command).start()
        # This second guard proves an omitted/unmocked command cannot escape.
        mock.patch.object(subprocess, 'run', side_effect=AssertionError('Real external command attempted')).start()

    def command(self, *args, capture=False):
        self.calls.append(tuple(args))
        if self.failure is not None and self.failure(args):
            raise subprocess.CalledProcessError(1, list(args))
        if args[:2] == ('git', 'rev-parse'): return self.head + '\n'
        if args[:2] == ('git', 'status'): return self.dirty
        if len(args) > 1 and args[1] == '--public-key': return self.key + '\n'
        if args[:2] == ('otool', '-L'): return '/System/Library/Frameworks/AVFoundation.framework/AVFoundation\n'
        if args[:2] == ('otool', '-l'): return 'Apple system linkage only\n'
        if args == ('bash', 'test-fullscreen.sh'):
            if self.change_after_checks == 'head': self.head = 'b' * 40
            if self.change_after_checks == 'dirty': self.dirty = ' M Sources/changed-during-tests.swift\n'
        if args[0] == 'codesign' and '--sign' in args and args[args.index('--sign') + 1] == '-':
            target = Path(args[-1])
            if target.suffix == '.app':
                (target / 'Contents/fake-ad-hoc-signature').write_text('not a Developer ID signature')
            elif target.suffix == '.dmg':
                target.write_bytes(target.read_bytes() + b' + final ad-hoc DMG signature')
        if args[:2] == ('ditto', '-c'):
            Path(args[-1]).write_bytes(b'synthetic temporary submission, not a ZIP')
        elif args[0] == 'ditto':
            if self.is_preview:
                self.assertTrue((self.app / 'Contents/fake-ad-hoc-signature').is_file(), 'Preview copied before ad-hoc signing')
            else:
                self.assertTrue((self.app / 'Contents/fake-staple-ticket').is_file(), 'App copied before stapling')
            shutil.copytree(Path(args[1]), Path(args[2]), symlinks=True)
        elif args[:2] == ('hdiutil', 'create'):
            stage = Path(args[args.index('-srcfolder') + 1])
            expected = {'Luma Player.app', 'Applications'} | ({'INSTALL.md'} if self.is_preview else set())
            self.assertEqual({p.name for p in stage.iterdir()}, expected)
            self.assertTrue((stage / 'Applications').is_symlink())
            self.assertEqual((stage / 'Applications').readlink(), Path('/Applications'))
            if self.is_preview:
                self.assertEqual((stage / 'INSTALL.md').read_text(), self.instructions)
                self.assertIn('Unnotarized Preview', args[args.index('-volname') + 1])
                self.assertFalse((stage / 'Luma Player.app/Contents/fake-staple-ticket').exists())
            else:
                self.assertTrue((stage / 'Luma Player.app/Contents/fake-staple-ticket').is_file())
            self.assertEqual(args[args.index('-format') + 1], 'UDZO')
            self.assertEqual(args[args.index('-fs') + 1], 'HFS+')
            self.stage_checked = True
            Path(args[-1]).write_bytes(b'synthetic DMG before staple')
        elif args[:3] == ('xcrun', 'notarytool', 'submit'):
            self.assertTrue(Path(args[3]).is_file())
            status = self.statuses[self.notary_count]
            self.notary_count += 1
            return json.dumps({'status': status})
        elif args[:3] == ('xcrun', 'stapler', 'staple'):
            target = Path(args[-1])
            if target.suffix == '.app':
                (target / 'Contents/fake-staple-ticket').write_bytes(b'app ticket')
            else:
                target.write_bytes(target.read_bytes() + b' + final DMG staple ticket')
        elif len(args) > 1 and args[1] == '--sign':
            payload, destination = Path(args[2]), Path(args[3])
            destination.write_text(json.dumps({'payload': base64.b64encode(payload.read_bytes()).decode(), 'signature': 'not-a-real-signature'}))
            if self.signer_failure:
                raise subprocess.CalledProcessError(1, list(args))
        return '' if capture else None

    def prepare(self, sha=None, identity='Developer ID Application: Synthetic Test'):
        self.is_preview = False
        return release.main(['prepare', '--reviewed-sha', sha or self.sha, '--notes', 'RELEASE_NOTES.md',
            '--identity', identity, '--notary-profile', 'synthetic-profile'])

    def prepare_preview(self, sha=None):
        self.is_preview = True
        self.image = self.destination / 'Luma-7-unnotarized-preview.dmg'
        return release.main(['prepare-preview', '--reviewed-sha', sha or self.sha, '--notes', 'RELEASE_NOTES.md'])

    def index(self, prefix, target_suffix=None):
        for index, call in enumerate(self.calls):
            if call[:len(prefix)] == prefix and (target_suffix is None or call[-1].endswith(target_suffix)):
                return index
        self.fail('Missing command ' + repr((prefix, target_suffix)))

    def assert_no_release(self):
        self.assertFalse(self.manifest.exists())
        self.assertFalse((self.destination / '.manifest.json.pending').exists())
        self.assertFalse((self.destination / 'payload.json').exists())
        self.assertFalse(self.image.exists())
        self.assertEqual(list(self.destination.glob('.dmg-work-*')), [])

    def test_complete_dmg_plan_hashes_final_stapled_bytes(self):
        self.assertEqual(self.prepare(), 0)
        payload = json.loads((self.destination / 'payload.json').read_text())
        self.assertTrue(self.stage_checked)
        self.assertEqual(payload['url'], 'https://github.com/example/luma-releases/releases/download/v0.6.0/Luma-7.dmg')
        self.assertEqual(payload['size'], self.image.stat().st_size)
        self.assertEqual(payload['sha256'], hashlib.sha256(self.image.read_bytes()).hexdigest())
        self.assertTrue(self.image.read_bytes().endswith(b' + final DMG staple ticket'))
        envelope = json.loads(self.manifest.read_text())
        self.assertEqual(base64.b64decode(envelope['payload']), (self.destination / 'payload.json').read_bytes())
        self.assertEqual(json.loads((self.destination / 'review.json').read_text())['reviewed_sha'], self.sha)
        self.assertEqual({p.name for p in self.destination.iterdir()}, {'Luma-7.dmg', 'payload.json', 'manifest.json', 'review.json'})
        app_notary = self.index(('xcrun', 'notarytool', 'submit'))
        app_staple = self.index(('xcrun', 'stapler', 'staple'), '.app')
        app_validate = self.index(('xcrun', 'stapler', 'validate'), '.app')
        create = self.index(('hdiutil', 'create'))
        image_sign = next(i for i, c in enumerate(self.calls) if c[:2] == ('codesign', '--force') and c[-1].endswith('.dmg'))
        image_notary = next(i for i, c in enumerate(self.calls) if c[:3] == ('xcrun', 'notarytool', 'submit') and c[3].endswith('.dmg'))
        image_staple = self.index(('xcrun', 'stapler', 'staple'), '.dmg')
        image_validate = self.index(('xcrun', 'stapler', 'validate'), '.dmg')
        image_integrity = self.index(('hdiutil', 'verify'), '.dmg')
        manifest_sign = next(i for i, c in enumerate(self.calls) if len(c) > 1 and c[1] == '--sign')
        sequence = [app_notary, app_staple, app_validate, create, image_sign, image_notary, image_staple, image_validate, image_integrity, manifest_sign]
        self.assertEqual(sequence, sorted(sequence))
        self.assertIn(('bash', 'test-fullscreen.sh'), self.calls)
        self.assertIn(('bash', 'test-opus.sh'), self.calls)
        self.assertIn(('bash', 'test-release-plan.sh'), self.calls)

    def test_preview_is_explicit_unnotarized_and_hashes_final_signed_bytes(self):
        self.assertEqual(self.prepare_preview(), 0)
        self.assertTrue(self.stage_checked)
        self.assertEqual(self.notary_count, 0)
        self.assertFalse(any(c[:2] in [('xcrun', 'notarytool'), ('xcrun', 'stapler')] for c in self.calls))
        signatures = [c for c in self.calls if c[0] == 'codesign' and '--sign' in c]
        self.assertEqual(len(signatures), 2)
        self.assertTrue(all(c[c.index('--sign') + 1] == '-' and '--timestamp' not in c for c in signatures))
        self.assertFalse(any('--generate-key' in c for c in self.calls))
        payload = json.loads((self.destination / 'payload.json').read_text())
        self.assertEqual(payload['url'], 'https://github.com/example/luma-releases/releases/download/v0.6.0/Luma-7-unnotarized-preview.dmg')
        self.assertEqual(payload['sha256'], hashlib.sha256(self.image.read_bytes()).hexdigest())
        self.assertEqual(payload['size'], self.image.stat().st_size)
        self.assertTrue(self.image.read_bytes().endswith(b' + final ad-hoc DMG signature'))
        envelope = json.loads(self.manifest.read_text())
        self.assertEqual(base64.b64decode(envelope['payload']), (self.destination / 'payload.json').read_bytes())
        review = json.loads((self.destination / 'review.json').read_text())
        self.assertEqual(review['distribution'], 'unnotarized-preview')
        self.assertEqual(review['reviewed_sha'], self.sha)
        self.assertEqual(review['artifact'], self.image.name)
        self.assertEqual({p.name for p in self.destination.iterdir()}, {self.image.name, 'payload.json', 'manifest.json', 'review.json'})
        final_verify = self.index(('hdiutil', 'verify'), '.dmg')
        manifest_sign = next(i for i, c in enumerate(self.calls) if len(c) > 1 and c[1] == '--sign')
        self.assertLess(final_verify, manifest_sign)

    def test_preview_requires_manual_install_instructions(self):
        (self.root / 'INSTALL.md').unlink()
        with self.assertRaisesRegex(RuntimeError, 'INSTALL.md'): self.prepare_preview()
        self.assertFalse(any(c[0] in ('bash','codesign','hdiutil','ditto') for c in self.calls))
        self.assert_no_release()

    def test_preview_failure_cleanup_and_no_notary_fallback(self):
        for failure in [
            lambda c: c[:2] == ('hdiutil', 'create'),
            lambda c: c[:2] == ('hdiutil', 'verify'),
            lambda c: c[0] == 'codesign' and '--sign' in c and c[-1].endswith('.dmg'),
            lambda c: len(c) > 1 and c[1] == '--sign'
        ]:
            with self.subTest(failure=failure):
                self.failure = failure
                with self.assertRaises(subprocess.CalledProcessError): self.prepare_preview()
                self.assert_no_release()
                self.assertFalse(any(c[:2] in [('xcrun', 'notarytool'), ('xcrun', 'stapler')] for c in self.calls))
                if self.destination.exists(): self.destination.rmdir()
                self.calls.clear()

    def test_preview_preserves_review_and_pinned_key_gates(self):
        for changed in ['sha', 'head', 'dirty', 'key', 'after-head', 'after-dirty']:
            with self.subTest(changed=changed):
                self.head = 'b' * 40 if changed == 'head' else self.sha
                self.dirty = ' M Sources/example.swift\n' if changed == 'dirty' else ''
                self.key = 'wrong public key' if changed == 'key' else self.config['publicEdKey']
                self.change_after_checks = changed[6:] if changed.startswith('after-') else None
                with self.assertRaises(RuntimeError): self.prepare_preview(sha='abc' if changed == 'sha' else None)
                self.assertFalse(any(c[0] in ('codesign','hdiutil','ditto') for c in self.calls))
                self.assert_no_release()
                self.calls.clear()

    def test_app_notary_rejection_stops_before_staging(self):
        self.statuses[0] = 'Invalid'
        with self.assertRaisesRegex(RuntimeError, 'app notarization'): self.prepare()
        self.assertFalse(any(c[:2] == ('hdiutil', 'create') for c in self.calls))
        self.assertFalse(any(c[:3] == ('xcrun', 'stapler', 'staple') for c in self.calls))
        self.assert_no_release()

    def test_dmg_notary_rejection_removes_unfinished_image(self):
        self.statuses[1] = 'Rejected'
        with self.assertRaisesRegex(RuntimeError, 'disk image notarization'): self.prepare()
        self.assertFalse(any(c[:3] == ('xcrun', 'stapler', 'staple') and c[-1].endswith('.dmg') for c in self.calls))
        self.assert_no_release()

    def test_staple_and_validation_failures_never_create_manifest(self):
        for operation, suffix in [('staple','.app'), ('validate','.app'), ('staple','.dmg'), ('validate','.dmg')]:
            with self.subTest(operation=operation, artifact=suffix):
                self.failure = lambda c, op=operation, ext=suffix: c[:3] == ('xcrun','stapler',op) and c[-1].endswith(ext)
                with self.assertRaises(subprocess.CalledProcessError): self.prepare()
                self.assert_no_release()
                if self.destination.exists(): self.destination.rmdir()
                self.notary_count = 0
                self.calls.clear()

    def test_partial_manifest_from_failed_signer_is_not_exposed(self):
        self.signer_failure = True
        with self.assertRaises(subprocess.CalledProcessError): self.prepare()
        self.assert_no_release()

    def test_failed_image_creation_signing_or_submission_never_creates_manifest(self):
        failures = [
            lambda c: c[:2] == ('hdiutil', 'create'),
            lambda c: c[:2] == ('hdiutil', 'verify'),
            lambda c: c[:2] == ('codesign', '--force') and c[-1].endswith('.dmg'),
            lambda c: c[:3] == ('xcrun', 'notarytool', 'submit') and c[3].endswith('.dmg')
        ]
        for index, failure in enumerate(failures):
            with self.subTest(stage=index):
                self.failure = failure
                with self.assertRaises(subprocess.CalledProcessError): self.prepare()
                self.assert_no_release()
                if self.destination.exists(): self.destination.rmdir()
                self.notary_count = 0
                self.calls.clear()

    def test_inputs_changed_during_checks_are_not_packaged(self):
        for changed in ['head', 'dirty']:
            with self.subTest(changed=changed):
                self.change_after_checks = changed
                with self.assertRaises(RuntimeError): self.prepare()
                self.assertIn(('bash', 'test-fullscreen.sh'), self.calls)
                self.assertFalse(any(c[0] in ('codesign','hdiutil','ditto') for c in self.calls))
                self.assert_no_release()
                self.head, self.dirty = self.sha, ''
                self.calls.clear()

    def test_preflight_gates_prevent_build_and_packaging(self):
        cases = [('malformed SHA', dict(sha='abc')), ('different HEAD', {}), ('dirty tree', {}), ('wrong Keychain key', {}), ('nonproduction identity', dict(identity='-'))]
        for name, arguments in cases:
            with self.subTest(name=name):
                self.head = 'b' * 40 if name == 'different HEAD' else self.sha
                self.dirty = ' M Sources/example.swift\n' if name == 'dirty tree' else ''
                self.key = 'wrong key' if name == 'wrong Keychain key' else self.config['publicEdKey']
                with self.assertRaises(RuntimeError): self.prepare(**arguments)
                self.assertFalse(any(c[0] in ('bash','codesign','hdiutil','ditto') or c[:2] == ('xcrun','notarytool') for c in self.calls))
                self.assert_no_release()
                self.calls.clear()

    def test_existing_release_directory_is_preserved(self):
        self.destination.mkdir(parents=True)
        sentinel = self.destination / 'owner-file'
        sentinel.write_text('preserve')
        with self.assertRaises(FileExistsError): self.prepare()
        self.assertEqual(sentinel.read_text(), 'preserve')
        self.assertFalse(any(c[0] in ('codesign','hdiutil','ditto') for c in self.calls))


if __name__ == '__main__':
    unittest.main(verbosity=2)
