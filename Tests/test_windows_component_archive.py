#!/usr/bin/env python3
"""Immutable transport reconstruction, distinct from runtime approval."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('archive_helper', Path(__file__).resolve().parents[1] / 'Scripts/prepare-libvlc-windows-archive.py')
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class ComponentArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.component = self.root / 'component'
        self.component.mkdir()
        for name, data in {'libvlc.dll': b'fixture DLL', 'Sources/CorrespondingSource.tar': b'fixture source',
                           'Notices/' + 'a' * 110 + '.txt': b'long-path notice'}.items():
            path = self.component / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        self.manifest = {'schema': 1, 'package': helper.PACKAGE, 'developmentOnly': True,
                         'redistributionApproved': False, 'files': helper.regular_tree(self.component)}
        self.write_manifest()

    def write_manifest(self):
        (self.component / helper.MANIFEST).write_text(json.dumps(self.manifest))

    def test_archive_ignores_host_metadata_and_preserves_only_inventoried_bytes(self):
        first, second = self.root / 'one.tar', self.root / 'two.tar'
        helper.create(self.component, first)
        for path in self.component.rglob('*'):
            os.utime(path, (123456789, 123456789))
            if path.is_file():
                path.chmod(0o600)
        helper.create(self.component, second)
        self.assertEqual(first.read_bytes(), second.read_bytes())
        with tarfile.open(first) as archive:
            self.assertEqual(archive.getnames(), sorted(self.manifest['files']))
            for item in archive:
                self.assertEqual((item.uid, item.gid, item.mtime, item.mode, item.uname, item.gname), (0, 0, 0, 0o644, '', ''))
                self.assertEqual(hashlib.sha256(archive.extractfile(item).read()).hexdigest(), self.manifest['files'][item.name]['sha256'])

    def test_unapproved_input_is_not_relabelled_and_existing_output_is_preserved(self):
        output = self.root / 'existing.tar'; output.write_bytes(b'preserve')
        with self.assertRaises(ValueError): helper.create(self.component, output)
        self.assertEqual(output.read_bytes(), b'preserve')
        self.manifest['developmentOnly'] = False; self.write_manifest()
        with self.assertRaises(ValueError): helper.create(self.component, self.root / 'bad.tar')

    def test_missing_changed_and_extra_files_reject(self):
        path = self.component / 'libvlc.dll'
        path.write_bytes(b'changed')
        with self.assertRaises(ValueError): helper.create(self.component, self.root / 'bad.tar')
        path.write_bytes(b'fixture DLL')
        extra = self.component / 'extra.dll'; extra.write_bytes(b'extra')
        with self.assertRaises(ValueError): helper.create(self.component, self.root / 'bad.tar')
        extra.unlink(); path.unlink()
        with self.assertRaises(ValueError): helper.create(self.component, self.root / 'bad.tar')

    def test_case_conflicts_reject_on_case_sensitive_source_hosts(self):
        path = self.component / 'LIBVLC.dll'
        if path.exists():
            self.skipTest('Case-insensitive filesystem cannot create this input')
        path.write_bytes(b'conflicting DLL')
        with self.assertRaises(ValueError): helper.regular_tree(self.component)

    @unittest.skipUnless(hasattr(os, 'symlink'), 'symlinks unavailable')
    def test_symlinks_and_output_inside_component_reject(self):
        try:
            (self.component / 'outside').symlink_to(self.root, target_is_directory=True)
        except OSError as error:
            if os.name == 'nt' and getattr(error, 'winerror', None) == 1314:
                self.skipTest('Runner lacks Windows symlink creation privilege')
            raise
        with self.assertRaises(ValueError): helper.create(self.component, self.root / 'bad.tar')
        (self.component / 'outside').unlink()
        with self.assertRaises(ValueError): helper.create(self.component, self.component / 'bad.tar')


if __name__ == '__main__':
    unittest.main()
