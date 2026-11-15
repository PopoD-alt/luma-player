#!/usr/bin/env python3
"""Isolated preparation/gate tests; no downloads, real packaging, or native execution."""
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest import mock
import zipfile
import sys

sys.dont_write_bytecode = True

spec = importlib.util.spec_from_file_location("prepare_libvlc", Path(__file__).with_name("prepare-libvlc.py"))
lib = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lib)


class PreparationTests(unittest.TestCase):
    def test_all_current_candidates_are_blocked_for_distribution(self):
        for record in lib.registry()["packages"].values():
            with self.assertRaisesRegex(ValueError, "Redistribution blocked"):
                lib.distribution_check(record)

    def test_release_rejected_before_download_or_staging(self):
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(lib, "obtain") as obtain:
            destination = Path(temp) / "runtime"
            with self.assertRaisesRegex(ValueError, "Redistribution blocked"):
                lib.prepare("macos-vlckit-3.7.3", destination, False)
            obtain.assert_not_called()
            self.assertFalse(destination.exists())

    def test_zip_escape_and_tar_symlink_escape_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with zipfile.ZipFile(root / "bad.zip", "w") as archive:
                archive.writestr("build/x64/../../escaped", "x")
            with self.assertRaises(ValueError):
                lib.extract_archive(root / "bad.zip", root / "zip", {"kind": "zip", "archivePrefix": "build/x64/"})
            with tarfile.open(root / "bad.tar", "w") as archive:
                item = tarfile.TarInfo("lib/link")
                item.type = tarfile.SYMTYPE
                item.linkname = "../../escaped"
                archive.addfile(item)
            with self.assertRaises(ValueError):
                lib.extract_archive(root / "bad.tar", root / "tar", {"kind": "tar", "archivePrefix": "lib/"})

    def test_cached_wrong_hash_is_not_used(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "test.zip").write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "hash differs"):
                lib.obtain({"url": "https://download.videolan.org/test.zip", "filename": "test.zip", "sha256": "0" * 64}, root)

    def test_forged_development_manifest_does_not_approve_distribution(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "libvlc.dll").write_bytes(b"test")
            record = {"artifact": {"sha256": "a" * 64}, "runtimeEntry": "libvlc.dll", "audit": {
                "status": "development-only", "blockers": ["Incomplete native sources"]}}
            manifest = {"package": "test", "artifactSha256": "a" * 64, "developmentOnly": False,
                        "redistributionApproved": True, "files": lib.inventory(root)}
            (root / lib.MANIFEST).write_text(json.dumps(manifest))
            with mock.patch.object(lib, "package", return_value=record):
                with self.assertRaisesRegex(ValueError, "Redistribution blocked"):
                    lib.verify(root, True)

    def test_extra_runtime_file_and_modified_library_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            library = root / "libvlc.dll"
            library.write_bytes(b"original")
            record = {"artifact": {"sha256": "b" * 64}, "runtimeEntry": "libvlc.dll"}
            manifest = {"package": "test", "artifactSha256": "b" * 64, "developmentOnly": True, "files": lib.inventory(root)}
            (root / lib.MANIFEST).write_text(json.dumps(manifest))
            with mock.patch.object(lib, "package", return_value=record):
                self.assertTrue(lib.verify(root)["developmentOnly"])
                extra = root / "unreviewed-plugin.dll"
                extra.write_bytes(b"extra")
                with self.assertRaisesRegex(ValueError, "inventory differs"):
                    lib.verify(root)
                extra.unlink()
                library.write_bytes(b"modified")
                with self.assertRaisesRegex(ValueError, "inventory differs"):
                    lib.verify(root)


if __name__ == "__main__":
    unittest.main(verbosity=2)
