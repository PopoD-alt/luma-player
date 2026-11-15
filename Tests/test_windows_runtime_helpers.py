#!/usr/bin/env python3
"""Original source-audit regressions; no runtime, network or installed compiler is used."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

REPO=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('windows_audit',REPO/'Scripts/prepare-libvlc-windows-audit.py')
audit=importlib.util.module_from_spec(spec);spec.loader.exec_module(audit)

class WindowsRuntimeHelpers(unittest.TestCase):
    def test_nested_automake_dependencies_use_compile_directory(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);source=root/'vlc';build=source/'build/src'
            (source/'src/win32').mkdir(parents=True);(source/'include').mkdir()
            unit=source/'src/win32/thread.c';header=source/'include/threads.h'
            unit.write_text('/* Original LGPL example */');header.write_text('/* Original header */')
            dep=build/'win32/.deps/thread.Plo';dep.parent.mkdir(parents=True)
            dep.write_text('win32/thread.lo: ../../src/win32/thread.c \\\n ../../include/threads.h\n')
            files,selected=audit.dependencies(build,source,{'.Plo'})
            self.assertEqual(files,['win32/.deps/thread.Plo']);self.assertEqual(selected,{unit.resolve(),header.resolve()})

    def test_opening_license_grant_and_later_license_reporting_differ(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'sample.c'
            path.write_text('/* GNU Lesser General Public License, version 2.1 */\nconst char *notice="GNU General Public License";')
            self.assertFalse(audit.gpl_header(path))
            path.write_text('/* GNU General\n * Public License, version 2 */')
            self.assertTrue(audit.gpl_header(path))

    def test_convenience_library_implementation_is_in_source_closure(self):
        with tempfile.TemporaryDirectory() as folder:
            text='libfirst_plugin_la_SOURCES=entry.c\nlibfirst_plugin_la_LIBADD=libhelper.la\nlibhelper_la_SOURCES=helper.cpp\nlibhelper_la_LIBADD=libfirst_plugin.la\n'
            with patch.object(audit,'command',return_value=text):
                mapping=audit.source_mapping(Path(folder),Path(folder)/'query.mk')
            self.assertEqual(mapping['libfirst_plugin'],['entry.c','helper.cpp'])

    def test_staging_cannot_recurse_into_source_or_audit_inputs(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);inputs=[root/'source',root/'audit']
            for invalid in [root/'source/component',root/'audit/component']:
                with self.assertRaises(ValueError):audit.validate_destination(invalid,inputs)
            destination=root/'component';self.assertEqual(audit.validate_destination(destination,inputs),destination.resolve())
            destination.mkdir()
            with self.assertRaises(ValueError):audit.validate_destination(destination,inputs)

if __name__=='__main__':unittest.main()
