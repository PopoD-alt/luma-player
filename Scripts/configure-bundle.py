#!/usr/bin/env python3
"""Apply publisher and public updater settings to the review/release bundle."""
import base64
import json
from pathlib import Path
import plistlib
import sys
from urllib.parse import urlsplit

root = Path(__file__).resolve().parent.parent
config = json.loads((root / 'ReleaseConfig.json').read_text())
with (root / 'Info.plist').open('rb') as stream:
    info = plistlib.load(stream)
info['CFBundleIdentifier'] = config['bundleIdentifier']
info['NSHumanReadableCopyright'] = 'Copyright © 2026 ' + config['publisher']
info['LumaPublisher'] = config['publisher']
feed, key = config['feedURL'], config['publicEdKey']
if feed or key:
    url = urlsplit(feed)
    if url.scheme != 'https' or not url.hostname or url.username or url.password:
        raise SystemExit('Set a credential-free HTTPS feedURL in ReleaseConfig.json.')
    try:
        valid = len(base64.b64decode(key, validate=True)) == 32
    except ValueError:
        valid = False
    if not valid:
        raise SystemExit('publicEdKey must be a base64-encoded 32-byte Ed25519 public key.')
    info.update(LumaUpdateFeedURL=feed, LumaUpdatePublicKey=key)
with Path(sys.argv[1]).open('wb') as stream:
    plistlib.dump(info, stream)
