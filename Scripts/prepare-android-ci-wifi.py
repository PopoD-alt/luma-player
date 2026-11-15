#!/usr/bin/env python3
"""Establish the Wi-Fi-only precondition on a disposable GitHub Actions emulator.

This is never called by local integration runs or by the application. Raw device
output stays in memory; evidence contains only fixed fields, counts and booleans.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import time


class PreparationError(RuntimeError):
    """Code-owned failure; never attach raw adb output."""


def summarize_network(connectivity, wifi_status):
    active = re.search(r'^Active default network:\s*(\d+)\s*$', connectivity, re.M)
    active_id = active.group(1) if active else None
    current = False
    agents = {}
    for line in connectivity.splitlines():
        if line.strip() == 'Current Networks:':
            current = True
            continue
        if not current:
            continue
        if line and not line[0].isspace():
            break
        match = re.match(r'\s*NetworkAgentInfo\{network\{(\d+)\}', line)
        transports = re.search(r'\bTransports:\s*(.*?)\s+Capabilities:', line)
        if match and transports:
            agents[match.group(1)] = set(re.findall(r'[A-Z][A-Z0-9_]*', transports.group(1)))
    active_transports = agents.get(active_id, set())
    wifi = lambda values: 'WIFI' in values and not {'VPN', 'CELLULAR'} & values
    return {
        'wifiEnabled': bool(re.search(r'^Wifi is enabled\s*$', wifi_status, re.M | re.I)),
        'wifiAssociated': bool(re.search(r'Supplicant state:\s*COMPLETED\b', wifi_status, re.I)),
        'wifiNetworkCount': sum(wifi(values) for values in agents.values()),
        'activeWifi': wifi(active_transports),
        'activeCellular': 'CELLULAR' in active_transports,
        'activeVpn': 'VPN' in active_transports,
    }


def execute_adb(arguments):
    try:
        result = subprocess.run(['adb', *arguments], capture_output=True, text=True, timeout=8)
    except (OSError, subprocess.SubprocessError):
        raise PreparationError('ADB_COMMAND_FAILED') from None
    if result.returncode != 0 or len(result.stdout) > 1024 * 1024:
        raise PreparationError('ADB_COMMAND_FAILED')
    return result.stdout


def prepare(report, *, enabled=False, environ=None, adb=None,
            clock=time.monotonic, sleep=time.sleep):
    environ = os.environ if environ is None else environ
    adb = execute_adb if adb is None else adb
    state = {'scope': 'disposable-github-actions-emulator-only', 'prepared': False,
             'cellularDataDisabled': False}
    began = clock()
    try:
        if not enabled or environ.get('GITHUB_ACTIONS') != 'true':
            raise PreparationError('CI_ONLY_GUARD')
        devices = re.findall(r'^(\S+)\s+(device|offline|unauthorized)\s*$', adb(['devices']), re.M)
        if len(devices) != 1 or devices[0][1] != 'device' or not re.fullmatch(r'emulator-\d+', devices[0][0]):
            raise PreparationError('SINGLE_EMULATOR_REQUIRED')
        prefix = ['-s', devices[0][0], 'shell']
        if adb([*prefix, 'getprop', 'ro.kernel.qemu']).strip() != '1':
            raise PreparationError('QEMU_REQUIRED')
        # Both guards above run before either mutation. All commands remain
        # explicitly scoped to the captured disposable emulator serial.
        adb([*prefix, 'svc', 'data', 'disable'])
        state['cellularDataDisabled'] = True
        adb([*prefix, 'svc', 'wifi', 'enable'])
        deadline = clock() + 45
        polls = 0
        while True:
            status = adb([*prefix, 'cmd', 'wifi', 'status'])
            connectivity = adb([*prefix, 'dumpsys', 'connectivity'])
            state.update(summarize_network(connectivity, status))
            polls += 1
            state['polls'] = polls
            if (state['wifiEnabled'] and state['wifiAssociated'] and state['wifiNetworkCount'] > 0
                    and state['activeWifi'] and not state['activeCellular'] and not state['activeVpn']):
                state['prepared'] = True
                state['result'] = 'WIFI_READY'
                return state
            if clock() >= deadline:
                raise PreparationError('WIFI_NOT_READY')
            sleep(.5)
    except PreparationError as error:
        state['result'] = str(error)
        raise
    finally:
        state['elapsedMs'] = max(0, round((clock() - began) * 1000))
        report = Path(report)
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text(json.dumps(state, indent=2) + '\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--prepare-disposable-emulator', action='store_true',
                        help='Explicitly enable CI-only emulator network setup; physical devices are always rejected.')
    parser.add_argument('--report', required=True, type=Path)
    arguments = parser.parse_args()
    try:
        state = prepare(arguments.report, enabled=arguments.prepare_disposable_emulator)
    except PreparationError as error:
        print('Android CI Wi-Fi precondition failed: ' + str(error))
        return 1
    print('Android CI Wi-Fi precondition: ' + state['result'])
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
