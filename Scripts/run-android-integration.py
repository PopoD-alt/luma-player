#!/usr/bin/env python3
"""Exercise the Android app against the production portable desktop server.

Only original synthetic fixtures are shared. Pairing metadata is private to a
temporary directory; the server is stopped before this script exits. No APK or
native runtime is published by this test harness.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]


def save_native_diagnostics(reports, pairing_code):
    """Bounded failure evidence; native engines can log media URLs, so redact them."""
    try:
        result = subprocess.run(['adb', 'logcat', '-d', '-t', '2000', '-s',
                                 'LumaPlayback:I', 'VLC:W', 'AndroidRuntime:E'],
                                text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                timeout=15)
        output = result.stdout[:2 * 1024 * 1024].replace(pairing_code, '[temporary pairing code]')
        output = re.sub(r'https?://[^\s<>\"\']+', '[media location]', output)
        output = re.sub(r'(?i)(token|nonce)=([a-z0-9_-]+)', r'\1=[redacted]', output)
        (reports / 'native-diagnostics.log').write_text(output)
    except (OSError, subprocess.SubprocessError):
        (reports / 'native-diagnostics.log').write_text('Bounded native log capture unavailable.\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--desktop', choices=('portable', 'macos'), default='portable')
    parser.add_argument('--exercise-video-recovery', action='store_true',
                        help='Debug-only native output fault; require one successful software recovery.')
    parser.add_argument('--app-apk', type=Path,
                        default=ROOT / 'Android/app/build/outputs/apk/debug/app-debug.apk')
    parser.add_argument('--test-apk', type=Path,
                        default=ROOT / 'Android/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk')
    parser.add_argument('--reports-directory', type=Path,
                        help='Owned local directory for this run; no pairing credentials are retained.')
    options = parser.parse_args()
    reports = ROOT / 'build/android-integration'
    if options.desktop == 'macos':
        reports = ROOT / 'build/android-mac-integration'
    if options.exercise_video_recovery:
        reports = reports.with_name(reports.name + '-recovery')
    if options.reports_directory:
        reports = options.reports_directory.resolve()
    reports.mkdir(parents=True, exist_ok=True)
    if options.desktop == 'macos':
        library = ROOT / 'build/tests/mac-sharing-host'
        library.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', '-target', 'arm64-apple-macos14.0',
                        '-parse-as-library', '-module-cache-path', str(ROOT / '.cache/modules'),
                        'Sources/SharingHTTP.swift', 'Sources/LocalSharingServer.swift', 'Tests/SharingHost.swift',
                        '-o', str(library)], check=True, cwd=ROOT)
        host = [str(library)]
    else:
        subprocess.run(['dotnet', 'build', str(ROOT / 'Windows/Luma.Core.Tests'), '-c', 'Release'], check=True, cwd=ROOT)
        library = ROOT / 'Windows/Luma.Core.Tests/bin/Release/net10.0/Luma.Core.Tests.dll'
        host = ['dotnet', str(library), '--sharing-test-host']
    debug = options.app_apk.resolve()
    tests = options.test_apk.resolve()
    if not debug.is_file() or not tests.is_file():
        raise RuntimeError('Build the development app and instrumentation APK first.')
    inputs = {'desktop': options.desktop, 'exerciseVideoRecovery': options.exercise_video_recovery}
    for name, path in [('appApk', debug), ('testApk', tests)]:
        with path.open('rb') as source:
            inputs[name] = {'name': path.name, 'bytes': path.stat().st_size,
                            'sha256': hashlib.file_digest(source, 'sha256').hexdigest()}
    (reports / 'test-inputs.json').write_text(json.dumps(inputs, indent=2) + '\n')
    with tempfile.TemporaryDirectory(prefix='luma-android-pair-') as working, (reports / 'sharing-host.log').open('w') as log:
        ready = Path(working) / 'ready.json'
        server = subprocess.Popen(host + [str(ROOT / 'Tests/Fixtures'), str(ready)],
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
                       '-e', 'hevcId', parameters['ids']['hevc10']]
            if options.exercise_video_recovery:
                command += ['-e', 'exerciseVideoRecovery', 'true']
            command += ['dev.sdas.luma.preview.test/androidx.test.runner.AndroidJUnitRunner']
            try:
                result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=240)
            except subprocess.TimeoutExpired as failure:
                output = failure.stdout or b''
                if isinstance(output, bytes):
                    output = output.decode('utf-8', errors='replace')
                (reports / 'instrumentation.log').write_text(output.replace(parameters['code'], '[temporary pairing code]'))
                save_native_diagnostics(reports, parameters['code'])
                raise RuntimeError('Android instrumentation exceeded its bounded test deadline; see instrumentation.log.') from None
            # Never retain the temporary pairing code in the saved evidence.
            output = result.stdout.replace(parameters['code'], '[temporary pairing code]')
            (reports / 'instrumentation.log').write_text(output)
            print(output, end='')
            if result.returncode or 'OK (1 test)' not in output or any(marker in output for marker in ('FAILURES!!!', 'INSTRUMENTATION_FAILED', 'Process crashed')):
                save_native_diagnostics(reports, parameters['code'])
                raise RuntimeError('Android end-to-end playback checks did not pass.')
            # Instrumentation captures playback before ActivityScenario closes;
            # a screenshot here would show the launcher instead of the app.
            shot = subprocess.run(['adb', 'exec-out', 'run-as', 'dev.sdas.luma.preview',
                                   'cat', 'cache/luma-android-review.png'], stdout=subprocess.PIPE, check=True)
            if not shot.stdout.startswith(b'\x89PNG\r\n\x1a\n'):
                raise RuntimeError('The in-app Android review capture is not a PNG.')
            (reports / 'Luma-Android-review.png').write_bytes(shot.stdout)
            print('PASS: Android actual ' + options.desktop + ' desktop pairing/list/range streaming, native AVC/HEVC decode/render, transport and no saved media download')
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
