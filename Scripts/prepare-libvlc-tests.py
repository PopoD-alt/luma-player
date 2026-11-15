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
audit_spec = importlib.util.spec_from_file_location("libvlc_macos_audit", Path(__file__).with_name("prepare-libvlc-macos-audit.py"))
audit = importlib.util.module_from_spec(audit_spec)
audit_spec.loader.exec_module(audit)


class PreparationTests(unittest.TestCase):
    def test_recursive_source_audit_rejects_gpl_helper_and_preserves_exact_lgpl_grant(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            modules = root / "modules"
            modules.mkdir()
            (modules / "entry.c").write_text('/* GNU Lesser General Public License */\n#include "helper.h"\n')
            (modules / "helper.h").write_text('/* under the terms of the GNU General Public License */\n')
            (modules / "ts.h").write_text('''/* under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation; either version 2.1 of the License, or later.
 * You should have received a copy of the GNU General Public License
 */\n''')
            (modules / "mixed.c").write_text('''/* under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation; either version 2.1 of the License.
 * Embedded algorithm under the GNU General Public License.
 */\n''')
            with mock.patch.object(audit, "VLC", root):
                found = audit.inspect_sources(["entry.c"])
                self.assertEqual([x["path"] for x in found if x["requiresExclusion"]], ["modules/helper.h"])
                clarified = audit.inspect_sources(["ts.h"])[0]
                self.assertTrue(clarified["gplNotice"])
                self.assertFalse(clarified["requiresExclusion"])
                self.assertIn("Explicit LGPL2.1+ grant", clarified["licenseClarification"])
                self.assertTrue(audit.inspect_sources(["mixed.c"])[0]["requiresExclusion"])

    def test_all_current_candidates_are_blocked_for_distribution(self):
        for record in lib.registry()["packages"].values():
            if record.get("audit", {}).get("status") == "approved-for-redistribution":
                continue
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

    def test_approved_inventory_binds_plugins_and_sources_even_if_manifest_rewritten(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "libvlc.dll").write_bytes(b"original core")
            plugin = root / "plugin.dll"
            plugin.write_bytes(b"reviewed plugin")
            source = root / "source.tar"
            source.write_bytes(b"complete source fixture")
            original = lib.inventory(root)
            record = {"artifact": {"sha256": "c" * 64}, "runtimeEntry": "libvlc.dll",
                      "runtimeInventorySha256": lib.inventory_sha256(original),
                      "correspondingSource": {"path": "source.tar", "sha256": lib.sha256(source)},
                      "audit": {"status": "approved-for-redistribution", "moduleLicensesReviewed": True,
                                "dependencyLicensesReviewed": True, "completeCorrespondingSources": True,
                                "replacementMechanismVerified": True}}
            manifest = {"package": "test", "artifactSha256": "c" * 64, "developmentOnly": False, "files": original}
            (root / lib.MANIFEST).write_text(json.dumps(manifest))
            with mock.patch.object(lib, "package", return_value=record):
                self.assertFalse(lib.verify(root, True)["developmentOnly"])
                plugin.write_bytes(b"unreviewed replacement for packaging")
                manifest["files"] = lib.inventory(root)
                (root / lib.MANIFEST).write_text(json.dumps(manifest))
                with self.assertRaisesRegex(ValueError, "centrally approved"):
                    lib.verify(root, True)
                plugin.write_bytes(b"reviewed plugin")
                source.unlink()
                manifest["files"] = lib.inventory(root)
                (root / lib.MANIFEST).write_text(json.dumps(manifest))
                with self.assertRaisesRegex(ValueError, "centrally approved"):
                    lib.verify(root, True)

    def test_custom_local_build_never_falls_back_to_download(self):
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(lib.urllib.request, "urlopen") as download:
            with self.assertRaisesRegex(ValueError, "custom build artifact is missing"):
                lib.obtain({"filename": "custom.tar", "localBuildOnly": True, "sha256": "0" * 64}, Path(temp))
            download.assert_not_called()

    def test_curated_artifact_keeps_its_versioned_notices(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive = root / "custom.tar"
            with tarfile.open(archive, "w") as output:
                for name, value in (("libvlc.dll", b"native fixture"),
                                    ("Notices/Luma-THIRD_PARTY_NOTICES.md", b"exact reviewed old notice")):
                    info = tarfile.TarInfo(name)
                    info.size = len(value)
                    output.addfile(info, io.BytesIO(value))
            record = {"artifact": {"sha256": lib.sha256(archive)}, "kind": "tar", "archivePrefix": "",
                      "runtimeEntry": "libvlc.dll", "noticesIncluded": True,
                      "audit": {"status": "development-only"}}
            with mock.patch.object(lib, "package", return_value=record), mock.patch.object(lib, "obtain", return_value=archive):
                lib.prepare("test", root / "stage", True)
            self.assertEqual((root / "stage/Notices/Luma-THIRD_PARTY_NOTICES.md").read_bytes(), b"exact reviewed old notice")


if __name__ == "__main__":
    unittest.main(verbosity=2)
