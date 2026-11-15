#!/usr/bin/env python3
"""Exercise the Android app against the production portable desktop server.

Only original synthetic fixtures are shared. Pairing metadata is private to a
temporary directory; the server is stopped before this script exits. No APK or
native runtime is published by this test harness.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]


def main():
    reports = ROOT / 'build/android-integration'
    reports.mkdir(parents=True, exist_ok=True)
    subprocess.run(['dotnet', 'build', str(ROOT / 'Windows/Luma.Core.Tests'), '-c', 'Release'], check=True, cwd=ROOT)
    library = ROOT / 'Windows/Luma.Core.Tests/bin/Release/net10.0/Luma.Core.Tests.dll'
    debug = ROOT / 'Android/app/build/outputs/apk/debug/app-debug.apk'
    tests = ROOT / 'Android/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk'
    if not debug.is_file() or not tests.is_file():
        raise RuntimeError('Build the development app and instrumentation APK first.')
    with tempfile.TemporaryDirectory(prefix='luma-android-pair-') as working, (reports / 'sharing-host.log').open('w') as log:
        ready = Path(working) / 'ready.json'
        server = subprocess.Popen(['dotnet', str(library), '--sharing-test-host', str(ROOT / 'Tests/Fixtures'), str(ready)],
                                  cwd=ROOT, stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 25
            while not ready.is_file():
                if server.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError('The actual desktop sharing test host did not start. See sharing-host.log.')
                time.sleep(.1)
            parameters = json.loads(ready.read_text())
            if os.environ.get('GITHUB_ACTIONS') == 'true':
                print('::add-mask::' + parameters['code'], flush=True)
            subprocess.run(['adb', 'install', '-r', str(debug)], check=True)
            subprocess.run(['adb', 'install', '-r', str(tests)], check=True)
            command = ['adb', 'shell', 'am', 'instrument', '-w',
                       '-e', 'serverUrl', 'http://10.0.2.2:' + str(parameters['port']),
                       '-e', 'pairCode', parameters['code'],
                       '-e', 'demoId', parameters['ids']['demo'],
                       '-e', 'hevcId', parameters['ids']['hevc10'],
                       'dev.sdas.luma.preview.test/androidx.test.runner.AndroidJUnitRunner']
            result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=240)
            # Never retain the temporary pairing code in the saved evidence.
            output = result.stdout.replace(parameters['code'], '[temporary pairing code]')
            (reports / 'instrumentation.log').write_text(output)
            print(output, end='')
            if result.returncode or 'OK (1 test)' not in output or any(marker in output for marker in ('FAILURES!!!', 'INSTRUMENTATION_FAILED', 'Process crashed')):
                raise RuntimeError('Android end-to-end playback checks did not pass.')
            shot = subprocess.run(['adb', 'exec-out', 'screencap', '-p'], stdout=subprocess.PIPE, check=True)
            (reports / 'Luma-Android-review.png').write_bytes(shot.stdout)
            print('PASS: Android actual desktop pairing/list/range streaming, native AVC/HEVC decode/render, transport and no saved media download')
            print('SCOPE: emulator/manual-address path; physical Wi-Fi discovery, phone hardware and physical sound still need device review')
        finally:
            if server.stdin:
                server.stdin.close()
            try:
                server.wait(timeout=8)
            except subprocess.TimeoutExpired:
                server.terminate()
                try:
                    server.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    server.kill()
                    server.wait()


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, subprocess.SubprocessError, OSError, ValueError, KeyError) as error:
        print('FAIL: ' + str(error), file=sys.stderr)
        sys.exit(1)
