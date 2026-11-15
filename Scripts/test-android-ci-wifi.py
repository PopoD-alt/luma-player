#!/usr/bin/env python3
"""Original isolated CI-network guard/privacy tests. Never invokes a real adb."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('android_ci_wifi', Path(__file__).with_name('prepare-android-ci-wifi.py'))
WIFI = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(WIFI)

SERIAL = 'emulator-5588'
SECRET_SSID = 'Original-Test-SSID-Do-Not-Retain'
SECRET_BSSID = 'a1:b2:c3:d4:e5:f6'
SECRET_IP = '10.88.77.66'
ENABLED = f'Wifi is enabled\nSSID: {SECRET_SSID}\nBSSID: {SECRET_BSSID}\nSupplicant state: COMPLETED\nIP: {SECRET_IP}\n'


def agent(network, transports='WIFI'):
    return ('  NetworkAgentInfo{network{' + str(network) + '} handle{1234} ni{WIFI CONNECTED extra: ' + SECRET_SSID +
            '} nc{[ Transports: ' + transports + ' Capabilities: INTERNET&NOT_VPN&TRUSTED ]} lp{LinkAddresses: [' + SECRET_IP + '/24]}}\n')


def connectivity(active=101, agents=None):
    return ('Active default network: ' + str(active) + '\nCurrent Networks:\n' +
            (agent(101) if agents is None else ''.join(agents)) +
            '\nNetwork Requests:\n  NetworkRequest [ Transports: WIFI Capabilities: INTERNET ]\n')


class Clock:
    def __init__(self):
        self.now = 0.0
        self.sleeps = []

    def __call__(self):
        return self.now

    def sleep(self, seconds):
        if seconds != .5:
            raise AssertionError('Unexpected polling interval')
        self.sleeps.append(seconds)
        self.now += seconds


class Adb:
    def __init__(self, *, devices=None, qemu='1\n', states=None):
        self.devices = 'List of devices attached\n' + SERIAL + '\tdevice\n' if devices is None else devices
        self.qemu = qemu
        self.states = [(connectivity(), ENABLED)] if states is None else states
        self.calls = []
        self.poll = 0

    def __call__(self, arguments):
        self.calls.append(list(arguments))
        if arguments == ['devices']:
            return self.devices
        if arguments[:3] != ['-s', SERIAL, 'shell']:
            raise AssertionError('A shell operation was not scoped to the verified emulator serial')
        command = arguments[3:]
        if command == ['getprop', 'ro.kernel.qemu']:
            return self.qemu
        if command in (['svc', 'data', 'disable'], ['svc', 'wifi', 'enable']):
            return ''
        state = self.states[min(self.poll, len(self.states) - 1)]
        if command == ['cmd', 'wifi', 'status']:
            return state[1]
        if command == ['dumpsys', 'connectivity']:
            self.poll += 1
            return state[0]
        raise AssertionError('Unexpected device command: ' + repr(command))

    def mutations(self):
        return [call for call in self.calls if call[3:4] == ['svc']]


class WifiPreparationTests(unittest.TestCase):
    def invoke(self, adb, clock=None, **kwargs):
        clock = clock or Clock()
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        report = Path(temporary.name) / 'owned-report.json'
        defaults = {'enabled': True, 'environ': {'GITHUB_ACTIONS': 'true'}, 'adb': adb, 'clock': clock, 'sleep': clock.sleep}
        defaults.update(kwargs)
        return report, defaults, clock

    def assert_private_report(self, report):
        text = report.read_text()
        for secret in [SECRET_SSID, SECRET_BSSID, SECRET_IP, SERIAL, 'NetworkAgentInfo', 'Supplicant state:', 'Transports:']:
            self.assertNotIn(secret, text)
        value = json.loads(text)
        fixed_strings = {'scope': {'disposable-github-actions-emulator-only'},
                         'result': {'WIFI_READY', 'WIFI_NOT_READY', 'CI_ONLY_GUARD', 'SINGLE_EMULATOR_REQUIRED', 'QEMU_REQUIRED', 'ADB_COMMAND_FAILED'}}
        for key, item in value.items():
            if key in fixed_strings:
                self.assertIn(item, fixed_strings[key])
            else:
                self.assertIn(type(item), (bool, int), key)
        return value

    def test_success_uses_verified_serial_only_and_saves_no_network_identifiers(self):
        adb = Adb(); report, arguments, _ = self.invoke(adb)
        result = WIFI.prepare(report, **arguments)
        self.assertTrue(result['prepared'])
        self.assertEqual(adb.calls[:4], [['devices'], ['-s', SERIAL, 'shell', 'getprop', 'ro.kernel.qemu'],
                                      ['-s', SERIAL, 'shell', 'svc', 'data', 'disable'], ['-s', SERIAL, 'shell', 'svc', 'wifi', 'enable']])
        self.assertEqual(len(adb.mutations()), 2)
        for call in adb.calls[1:]:
            self.assertEqual(call[:3], ['-s', SERIAL, 'shell'])
        saved = self.assert_private_report(report)
        self.assertEqual(saved['result'], 'WIFI_READY')
        self.assertTrue(saved['wifiEnabled'] and saved['wifiAssociated'] and saved['activeWifi'])
        self.assertFalse(saved['activeCellular'] or saved['activeVpn'])
        self.assertEqual(saved['wifiNetworkCount'], 1)

    def test_opt_in_and_exact_actions_environment_are_both_required(self):
        for enabled, environ in [(False, {'GITHUB_ACTIONS': 'true'}), (True, {}), (True, {'GITHUB_ACTIONS': 'false'}), (True, {'GITHUB_ACTIONS': 'TRUE'})]:
            with self.subTest(enabled=enabled, environ=environ):
                adb = Adb(); report, arguments, _ = self.invoke(adb, enabled=enabled, environ=environ)
                with self.assertRaisesRegex(WIFI.PreparationError, '^CI_ONLY_GUARD$'):
                    WIFI.prepare(report, **arguments)
                self.assertEqual(adb.calls, [])
                self.assertFalse(self.assert_private_report(report)['prepared'])
        adb = Adb(); report, arguments, _ = self.invoke(adb, environ=None)
        with patch.dict(WIFI.os.environ, {}, clear=True), self.assertRaisesRegex(WIFI.PreparationError, '^CI_ONLY_GUARD$'):
            WIFI.prepare(report, **arguments)
        self.assertEqual(adb.calls, [])

    def test_physical_ambiguous_missing_and_unauthorized_devices_never_mutate(self):
        for rows in ['physical-original-serial\tdevice\n', '', SERIAL+'\toffline\n', SERIAL+'\tunauthorized\n',
                     SERIAL+'\tdevice\nemulator-5590\tdevice\n', SERIAL+'\tdevice\nphysical-original-serial\tdevice\n',
                     SERIAL+'\tdevice\nphysical-original-serial\tunauthorized\n']:
            with self.subTest(rows=rows):
                adb = Adb(devices='List of devices attached\n'+rows); report, arguments, _ = self.invoke(adb)
                with self.assertRaisesRegex(WIFI.PreparationError, '^SINGLE_EMULATOR_REQUIRED$'):
                    WIFI.prepare(report, **arguments)
                self.assertEqual(adb.calls, [['devices']])
                self.assertFalse(self.assert_private_report(report)['cellularDataDisabled'])

    def test_qemu_must_be_proven_before_either_mutation(self):
        adb = Adb(qemu='0\n'); report, arguments, _ = self.invoke(adb)
        with self.assertRaisesRegex(WIFI.PreparationError, '^QEMU_REQUIRED$'):
            WIFI.prepare(report, **arguments)
        self.assertEqual(len(adb.calls), 2); self.assertEqual(adb.mutations(), [])
        self.assertFalse(self.assert_private_report(report)['prepared'])

    def test_current_wifi_default_and_completed_association_are_required(self):
        good = WIFI.summarize_network(connectivity(), ENABLED)
        self.assertTrue(good['activeWifi']); self.assertEqual(good['wifiNetworkCount'], 1)
        self.assertFalse(good['activeVpn'])  # NOT_VPN capability is not a VPN transport.
        for active, transports in [(102, 'CELLULAR'), (103, 'WIFI|VPN'), (104, 'WIFI|CELLULAR')]:
            summary = WIFI.summarize_network(connectivity(active, [agent(101), agent(active, transports)]), ENABLED)
            self.assertFalse(summary['activeWifi'])
            self.assertTrue(summary['activeCellular'] or summary['activeVpn'])
        self.assertFalse(WIFI.summarize_network(connectivity(999), ENABLED)['activeWifi'])
        self.assertFalse(WIFI.summarize_network(connectivity(), 'Wifi is disabled\nSupplicant state: DISCONNECTED')['wifiEnabled'])
        self.assertFalse(WIFI.summarize_network(connectivity(), 'Wifi is enabled\nSupplicant state: SCANNING')['wifiAssociated'])

    def test_requests_history_and_non_agent_mentions_do_not_count_as_current_wifi(self):
        examples = [
            'Active default network: 101\nPending Network Requests:\n'+agent(101),
            'Active default network: 101\nHistorical Networks:\n'+agent(101)+'Current Networks:\n\nNetwork Requests:\n',
            'Active default network: 101\nCurrent Networks:\n  NetworkRequest [ Transports: WIFI Capabilities: INTERNET ]\nPending Network Requests:\n'+agent(101),
            'Active default network: 101\nCurrent Networks:\n'+agent(102, 'CELLULAR')+'Network Requests:\n'+agent(101),
            'Active default network: 101\nCurrent Networks:\n  request for NetworkAgentInfo{network{101} nc{[ Transports: WIFI Capabilities: INTERNET ]}}\nNetwork Requests:\n',
        ]
        for dump in examples:
            with self.subTest(dump=dump):
                summary = WIFI.summarize_network(dump, ENABLED)
                self.assertFalse(summary['activeWifi']); self.assertEqual(summary['wifiNetworkCount'], 0)

    def test_polling_waits_for_actual_ready_state_without_repeating_mutations(self):
        states = [(connectivity(102, [agent(102, 'CELLULAR')]), ENABLED),
                  (connectivity(), 'Wifi is enabled\nSupplicant state: ASSOCIATING\n'), (connectivity(), ENABLED)]
        adb = Adb(states=states); report, arguments, clock = self.invoke(adb)
        result = WIFI.prepare(report, **arguments)
        self.assertTrue(result['prepared']); self.assertEqual(result['polls'], 3)
        self.assertEqual(clock.sleeps, [.5, .5]); self.assertEqual(len(adb.mutations()), 2)
        self.assertEqual(self.assert_private_report(report)['elapsedMs'], 1000)

    def test_timeout_is_bounded_and_does_not_repeat_or_add_mutations(self):
        adb = Adb(states=[(connectivity(102, [agent(102, 'CELLULAR')]), ENABLED)])
        report, arguments, clock = self.invoke(adb)
        with self.assertRaisesRegex(WIFI.PreparationError, '^WIFI_NOT_READY$'):
            WIFI.prepare(report, **arguments)
        saved = self.assert_private_report(report)
        self.assertFalse(saved['prepared']); self.assertEqual(saved['result'], 'WIFI_NOT_READY')
        self.assertEqual(clock.now, 45); self.assertLessEqual(saved['polls'], 92)
        self.assertEqual(adb.mutations(), [['-s', SERIAL, 'shell', 'svc', 'data', 'disable'], ['-s', SERIAL, 'shell', 'svc', 'wifi', 'enable']])

    def test_real_adb_wrapper_maps_failed_oversized_and_timeout_output_to_fixed_code(self):
        secret = SECRET_SSID+' '+SECRET_BSSID+' '+SECRET_IP
        for reply in [subprocess.CompletedProcess(['adb'], 1, secret, secret),
                      subprocess.CompletedProcess(['adb'], 0, 'x'*(1024*1024+1), secret)]:
            with patch.object(WIFI.subprocess, 'run', return_value=reply) as run:
                with self.assertRaisesRegex(WIFI.PreparationError, '^ADB_COMMAND_FAILED$'):
                    WIFI.execute_adb(['devices'])
                self.assertEqual(run.call_args.kwargs['timeout'], 8)
        with patch.object(WIFI.subprocess, 'run', side_effect=subprocess.TimeoutExpired(['adb'], 8, output=secret)):
            with self.assertRaisesRegex(WIFI.PreparationError, '^ADB_COMMAND_FAILED$'):
                WIFI.execute_adb(['devices'])


if __name__ == '__main__':
    unittest.main()
