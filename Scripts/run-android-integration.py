#!/usr/bin/env python3
"""Exercise the Android app against the production portable desktop server.

Only original synthetic fixtures are shared. Pairing metadata is private to a
temporary directory; the server is stopped before this script exits. No APK or
native runtime is published by this test harness.
"""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parents[1]
LOG_LIMIT = 1024 * 1024
LOG_LINE_LIMIT = 64 * 1024
LOG_TAGS = ['LumaPlayback:I', 'VLC:V', 'AndroidRuntime:E', 'libc:E']


def redact_diagnostics(text, pairing_code):
    """Sanitize before retaining evidence, including native URLs and structured secrets."""
    text = re.sub(r'(?i)https?:(?:/|\\/){2}[^\s<>\"\']*', '[media location]', text)
    # StreamRelay's native HTTP client may log only its random request target.
    text = re.sub(r'(?i)(/media/)[0-9a-f]{48}(?![0-9a-f])', r'\1[redacted nonce]', text)
    text = re.sub(r'''(?ix)
        (\b[a-z0-9_-]*(?:token|nonce)\b["']?\s*[:=]\s*)
        (?:"(?:\\.|[^"\\\r\n])*"?|'(?:\\.|[^'\\\r\n])*'?|[^\s,;}\]\&"']+)
        ''', r'\1[redacted]', text)
    text = re.sub(r'''(?i)(\bauthorization["']?\s*[:=]\s*["']?(?:bearer|basic)\s+)[^\s"']+''', r'\1[redacted]', text)
    return text.replace(pairing_code, '[temporary pairing code]') if pairing_code else text


class BoundedRedactedLog:
    """Only sanitized UTF-8 bytes enter the fixed prefix and rolling tail buffers."""
    def __init__(self, pairing_code, first_limit=LOG_LIMIT, tail_limit=LOG_LIMIT):
        if first_limit < 1 or tail_limit < 1:
            raise ValueError('Diagnostic bounds must be positive.')
        self.pairing_code = pairing_code
        self.first_limit, self.tail_limit = first_limit, tail_limit
        self.first, self.tail = bytearray(), bytearray()
        self.total = 0
        self.lock = threading.Lock()

    def append(self, text):
        sanitized = redact_diagnostics(text, self.pairing_code).encode('utf-8', errors='replace')
        with self.lock:
            self.total += len(sanitized)
            prefix = min(self.first_limit - len(self.first), len(sanitized))
            self.first.extend(sanitized[:prefix])
            self.tail.extend(sanitized[prefix:])
            if len(self.tail) > self.tail_limit:
                del self.tail[:-self.tail_limit]

    def render(self):
        with self.lock:
            first, tail, total = bytes(self.first), bytes(self.tail), self.total
        omitted = total - len(first) - len(tail)
        if omitted > 0:
            return (first.decode('utf-8', errors='replace') +
                    '\n[bounded capture omitted ' + str(omitted) + ' sanitized bytes]\n' +
                    tail.decode('utf-8', errors='replace'))
        return (first + tail).decode('utf-8', errors='replace')


def read_native_log(stream, evidence):
    """Drop an overlong logical line entirely, rather than split a secret across chunks."""
    discarding = False
    while True:
        line = stream.readline(LOG_LINE_LIMIT + 1)
        if not line:
            return
        if discarding:
            discarding = not line.endswith(b'\n')
            continue
        if len(line) > LOG_LINE_LIMIT:
            evidence.append('[overlong native log line omitted]\n')
            discarding = not line.endswith(b'\n')
            continue
        evidence.append(line.decode('utf-8', errors='replace'))


class NativeDiagnostics:
    def __init__(self, pairing_code):
        self.evidence = BoundedRedactedLog(pairing_code)
        self.process = self.reader = None
        self.fallback = False

    def start(self):
        try:
            self.process = subprocess.Popen(['adb', 'logcat', '-T', '1', '-v', 'threadtime', '-s', *LOG_TAGS],
                                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, bufsize=65536)
            self.reader = threading.Thread(target=self._read, daemon=True, name='luma-native-log-reader')
            self.reader.start()
        except (OSError, subprocess.SubprocessError, RuntimeError):
            self.reader = None
            self.fallback = True
            self.evidence.append('Live capture unavailable; using a bounded selected-tag snapshot.\n')

    def _read(self):
        try:
            read_native_log(self.process.stdout, self.evidence)
        except (OSError, ValueError):
            self.evidence.append('[native log reader closed]\n')

    def stop_and_save(self, reports):
        # Never wait on an adb/pipe reader indefinitely. No device logs are cleared.
        deadline = time.monotonic() + 3
        if self.process is not None:
            try:
                if self.process.poll() is None:
                    self.process.terminate()
                    try:
                        self.process.wait(timeout=min(1, max(.01, deadline - time.monotonic())))
                    except subprocess.TimeoutExpired:
                        self.process.kill()
                        self.process.wait(timeout=max(.01, deadline - time.monotonic()))
            except (OSError, subprocess.SubprocessError):
                self.evidence.append('[native log process did not exit normally]\n')
            if self.reader is not None:
                self.reader.join(timeout=max(0, deadline - time.monotonic()))
                if self.reader.is_alive():
                    self.evidence.append('[native log reader reached the three-second stop bound]\n')
            if self.process.stdout is not None and (self.reader is None or not self.reader.is_alive()):
                try:
                    self.process.stdout.close()
                except (OSError, ValueError):
                    pass
        if self.fallback:
            try:
                result = subprocess.run(['adb', 'logcat', '-d', '-t', '256', '-v', 'threadtime', '-s', *LOG_TAGS],
                                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=3)
                read_native_log(io.BytesIO(result.stdout), self.evidence)
            except (OSError, subprocess.SubprocessError):
                self.evidence.append('Bounded native log snapshot unavailable.\n')
        (reports / 'native-diagnostics.log').write_text(self.evidence.render(), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--desktop', choices=('portable', 'macos'), default='portable')
    parser.add_argument('--exercise-video-recovery', action='store_true',
                        help='Debug-only native output fault; require one successful software recovery.')
    parser.add_argument('--native-diagnostics', action='store_true',
                        help='Opt in to debug-only verbose native engine logs; evidence remains redacted and bounded.')
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
    inputs = {'desktop': options.desktop, 'exerciseVideoRecovery': options.exercise_video_recovery,
              'nativeDiagnostics': options.native_diagnostics}
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
            if options.native_diagnostics:
                command += ['-e', 'nativeDiagnostics', 'true']
            command += ['dev.sdas.luma.preview.test/androidx.test.runner.AndroidJUnitRunner']
            diagnostics = NativeDiagnostics(parameters['code'])
            diagnostics.start()
            try:
                try:
                    result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=240)
                except subprocess.TimeoutExpired as failure:
                    output = failure.stdout or b''
                    if isinstance(output, bytes):
                        output = output.decode('utf-8', errors='replace')
                    (reports / 'instrumentation.log').write_text(redact_diagnostics(output, parameters['code']))
                    raise RuntimeError('Android instrumentation exceeded its bounded test deadline; see instrumentation.log.') from None
                output = redact_diagnostics(result.stdout, parameters['code'])
                (reports / 'instrumentation.log').write_text(output)
                print(output, end='')
                if result.returncode or 'OK (1 test)' not in output or any(marker in output for marker in ('FAILURES!!!', 'INSTRUMENTATION_FAILED', 'Process crashed')):
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
                diagnostics.stop_and_save(reports)
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
