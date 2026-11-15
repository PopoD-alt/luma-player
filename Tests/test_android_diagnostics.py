#!/usr/bin/env python3
"""Original privacy/bounds/lifecycle tests; no emulator, media or real credentials."""
import importlib.util
import io
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('android_integration', ROOT/'Scripts/run-android-integration.py')
diagnostics = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diagnostics)


class AndroidDiagnosticsTests(unittest.TestCase):
    def test_secrets_redacted_before_bounded_storage(self):
        log = diagnostics.BoundedRedactedLog('123456', 512, 512)
        relay_nonce = 'a1' * 24
        playlist_id = 'f0679a0b-d520-4bc7-b74f-e5177b3f51ed'
        raw = ('code=123456 HTTP://private.example/123456/media?token=url-secret '
               r'https:\/\/private.example\/nonce-secret ' +
               'token=prefix123456suffix "access_token":"quoted token secret" '
               "NONCE='nonce secret' clientNonce=client-secret Authorization: Bearer bearer-secret "
               '\"Authorization\":\"Basic basic-secret\"\n' +
               'GET /media/' + relay_nonce + ' HTTP/1.1 item=' + playlist_id + '\n')
        log.append(raw)
        for secret in ['123456', 'private.example', 'url-secret', 'nonce-secret', 'prefix', 'suffix',
                       'quoted token secret', 'nonce secret', 'client-secret', 'bearer-secret', 'basic-secret', relay_nonce, 'http:', 'HTTP:', 'https:']:
            self.assertNotIn(secret, log.render())
            self.assertNotIn(secret.encode(), log.first + log.tail)
        self.assertIn('[temporary pairing code]', log.render())
        self.assertIn('[redacted]', log.render())
        self.assertIn('/media/[redacted nonce]', log.render())
        self.assertIn(playlist_id, log.render())

    def test_first_and_rolling_tail_are_independently_bounded(self):
        log = diagnostics.BoundedRedactedLog('123456', 256, 128)
        log.append('initial decoder stage\n')
        for i in range(1000):
            log.append(f'native stage {i}: http://private.example/a token=secret-{i} code=123456\n')
        log.append('final vout cleanup stage\n')
        self.assertEqual(len(log.first), 256)
        self.assertEqual(len(log.tail), 128)
        output = log.render()
        self.assertTrue(output.startswith('initial decoder stage\n'))
        self.assertTrue(output.endswith('final vout cleanup stage\n'))
        self.assertIn('bounded capture omitted', output)
        self.assertLessEqual(len(output.encode()), 512)
        self.assertNotIn('private.example', output)
        self.assertNotIn('secret-', output)
        self.assertNotIn('123456', output)

    def test_overlong_line_is_dropped_without_secret_fragments(self):
        log = diagnostics.BoundedRedactedLog('123456', 512, 512)
        huge = b'http://private.example/' + b'x' * (diagnostics.LOG_LINE_LIMIT * 2) + b'?token=hidden-secret\n'
        diagnostics.read_native_log(io.BytesIO(huge + b'final clean native stage\n'), log)
        self.assertEqual(log.render(), '[overlong native log line omitted]\nfinal clean native stage\n')

    def test_live_reader_keeps_startup_evidence_and_stops_bounded(self):
        real_popen = subprocess.Popen
        code = 'import time; print("startup decoder http://private.example/a nonce=live-secret 123456", flush=True); time.sleep(30)'
        def child(_command, **kwargs):
            self.assertEqual(_command[:7], ['adb', 'logcat', '-T', '1', '-v', 'threadtime', '-s'])
            return real_popen([sys.executable, '-c', code], **kwargs)
        with tempfile.TemporaryDirectory() as folder, patch.object(diagnostics.subprocess, 'Popen', side_effect=child):
            capture = diagnostics.NativeDiagnostics('123456')
            capture.start()
            deadline = time.monotonic() + 5
            while 'startup decoder' not in capture.evidence.render() and time.monotonic() < deadline:
                time.sleep(.01)
            self.assertIn('startup decoder', capture.evidence.render())
            started = time.monotonic()
            capture.stop_and_save(Path(folder))
            self.assertLess(time.monotonic() - started, 3.5)
            self.assertIsNotNone(capture.process.poll())
            output = (Path(folder)/'native-diagnostics.log').read_text()
            self.assertIn('startup decoder', output)
            for secret in ['private.example', 'live-secret', '123456']:
                self.assertNotIn(secret, output)

    def test_unavailable_live_capture_falls_back_without_raw_evidence(self):
        snapshot = subprocess.CompletedProcess(['adb'], 0, b'fallback token=snapshot-secret http://private.example/a 123456\n')
        with tempfile.TemporaryDirectory() as folder, patch.object(diagnostics.subprocess, 'Popen', side_effect=OSError), patch.object(diagnostics.subprocess, 'run', return_value=snapshot) as run:
            capture = diagnostics.NativeDiagnostics('123456'); capture.start(); capture.stop_and_save(Path(folder))
            self.assertEqual(run.call_args.kwargs['timeout'], 3)
            self.assertIn('256', run.call_args.args[0])
            output = (Path(folder)/'native-diagnostics.log').read_text()
            for secret in ['snapshot-secret', 'private.example', '123456']:
                self.assertNotIn(secret, output)
            self.assertIn('fallback', output)


if __name__ == '__main__':
    unittest.main()
