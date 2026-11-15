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
spec=importlib.util.spec_from_file_location('windows_libtool',REPO/'Scripts/prepare-libvlc-windows-libtool.py')
libtool=importlib.util.module_from_spec(spec);spec.loader.exec_module(libtool)

class WindowsRuntimeHelpers(unittest.TestCase):
    def test_core_snapshot_framework_does_not_enable_ffmpeg_encoders(self):
        vlc='#define ENABLE_SOUT 1\n'
        ffmpeg='#define CONFIG_ENCODERS 0\n'
        components='#define CONFIG_FLAC_ENCODER 0\n#define CONFIG_PNG_ENCODER 0\n#define CONFIG_AAC_ENCODER 0\n'
        result=audit.image_encoding_profile(vlc,ffmpeg,components)
        self.assertTrue(result['coreImageEncoderFrameworkEnabled'])
        self.assertFalse(result['ffmpegEncodersEnabled'])
        self.assertEqual(result['ffmpegEncoderComponentCount'],3)
        for inputs in [('#undef ENABLE_SOUT\n',ffmpeg,components),
                       (vlc,'#define CONFIG_ENCODERS 1\n',components),
                       (vlc,ffmpeg,components.replace('CONFIG_AAC_ENCODER 0','CONFIG_AAC_ENCODER 1')),
                       (vlc,ffmpeg,''),
                       (vlc,ffmpeg,'#define CONFIG_PNG_ENCODER 0\n')]:
            with self.assertRaises(ValueError):audit.image_encoding_profile(*inputs)

    def test_playback_only_curation_preserves_inputs_and_image_encoder(self):
        for name in ['access_output/libfile_plugin.dll','mux/libmp4_plugin.dll','stream_out/libtranscode_plugin.dll',
                     'stream_filter/librecord_plugin.dll','STREAM_OUT/libhttp_plugin.dll']:
            self.assertIsNotNone(audit.playback_only_exclusion(name))
        for name in ['access/libaccess_http_plugin.dll','demux/libmp4_plugin.dll','codec/libpng_plugin.dll',
                     'stream_filter/libprefetch_plugin.dll','audio_output/libwasapi_plugin.dll','video_output/libdirect3d11_plugin.dll']:
            self.assertIsNone(audit.playback_only_exclusion(name))

    def test_libtool_preserves_official_clang_runtime_and_is_idempotent(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'libtool.m4';path.write_text('prefix\n'+libtool.OLD+'\nsuffix\n')
            receipt=libtool.patch_macro(path)
            self.assertTrue(receipt['changed']);self.assertNotEqual(receipt['inputSHA256'],receipt['outputSHA256'])
            self.assertEqual(path.read_text(),'prefix\n'+libtool.NEW+'\nsuffix\n')
            self.assertFalse(libtool.patch_macro(path)['changed'])

    def test_libtool_unknown_or_ambiguous_parser_fails_without_change(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'libtool.m4'
            for text in ['unrecognized parser',libtool.OLD+'\n'+libtool.OLD]:
                path.write_text(text)
                with self.assertRaises(ValueError):libtool.patch_macro(path)
                self.assertEqual(path.read_text(),text)

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
