#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 sdas
# See ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt.
"""Focused source-selection tests; no third-party code is copied into tests."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec=importlib.util.spec_from_file_location('native_filter',Path(__file__).with_name('filter-native-modules.py'))
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)

class SourceFilterTests(unittest.TestCase):
    def test_lgpl_source_and_local_header_have_pinned_inventory(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);(root/'modules').mkdir();(root/'modules/demo.c').write_text('/* GNU Lesser General Public License */\n#include "detail.h"\n');(root/'modules/detail.h').write_text('/* permissive helper */\n')
            result=module.inspect(root,['demo.c']);self.assertEqual(2,len(result));self.assertTrue(all(len(item['sha256'])==64 and not item['gplNotice'] for item in result))
    def test_conditionally_included_gpl_header_is_conservatively_excluded(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);(root/'modules').mkdir();(root/'modules/demo.c').write_text('#ifdef SOME_PLATFORM\n#include "gpl.h"\n#endif\n');(root/'modules/gpl.h').write_text('/* GNU General Public License */\n')
            result=module.inspect(root,['demo.c']);self.assertTrue(any(item['gplNotice'] for item in result))
    def test_deep_license_override_is_not_hidden_past_notice_prefix(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);(root/'modules').mkdir();(root/'modules/demo.c').write_text('/*'+('x'*9000)+'*/\n#define VLC_MODULE_LICENSE VLC_LICENSE_GPL_2_PLUS\n')
            value=module.inspect(root,['demo.c'])[0];self.assertTrue(value['gplNotice']);self.assertTrue(value['unreviewedLicenseOverride'])
    def test_linked_convenience_library_sources_are_expanded_with_cycle_guard(self):
        with tempfile.TemporaryDirectory() as d:
            build=Path(d);(build/'modules').mkdir()
            (build/'modules/Makefile').write_text('libdemo_plugin_la_SOURCES = demo.c\nlibdemo_plugin_la_LIBADD = libcommon.la\nlibcommon_la_SOURCES = common.c\nlibcommon_la_LIBADD = libmore.la\nlibmore_la_SOURCES = more.c\nlibmore_la_LIBADD = libcommon.la\n')
            self.assertEqual(['common.c','demo.c','more.c'],module.mapping(build)['libdemo_plugin'])
    def test_unknown_explicit_module_license_fails_conservatively(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);(root/'modules').mkdir();(root/'modules/demo.c').write_text('#define VLC_MODULE_LICENSE CUSTOM_UNREVIEWED_LICENSE\n')
            self.assertTrue(module.inspect(root,['demo.c'])[0]['unreviewedLicenseOverride'])

if __name__=='__main__':unittest.main()
