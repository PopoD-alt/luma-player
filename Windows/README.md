# Luma Player for Windows — libVLC review

Luma keeps its native WPF interface, queue, bookmarks, resume positions, themes and fullscreen controls. Playback now goes directly to **libVLC 3** for local media. The app no longer makes a whole-file Matroska remux or an HEVC compatibility copy before playing a file, and that former 512 MiB gate does not apply to playback.

This branch is **0.7.0 Windows review build 4**, targeting Windows 11 x64 and .NET 10. Windows on ARM, other Windows versions, every codec/profile and physical audio output are not implied by a successful build. Read the native results for the exact commit before treating a path as verified. No production Windows release is published by the build script.

## Runtime requirement

Install the official **64-bit VLC 3** application separately, or set `LUMA_LIBVLC_PATH` to an absolute path to its `libvlc.dll`. The matching `libvlccore.dll` and `plugins` folder must remain beside it. For example:

```powershell
$env:LUMA_LIBVLC_PATH = 'C:\Program Files\VideoLAN\VLC\libvlc.dll'
& '.\Luma Player.exe'
```

The adapter checks the libVLC major version. An optional `<app>/libvlc/` runtime is considered only with a redistribution audit approval manifest. **The current review package does not bundle stock VLC or its plugins.** Runtime redistribution remains subject to the owner's package, dependency and source audit. A separately installed stock runtime used by development tests is not permission to redistribute it with Luma. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Keep all files from the Luma review folder together. The Windows review executable is unsigned and has no established trusted publisher reputation. Use the provenance and your local policy to decide whether to run it; do not disable system protection globally.

## Playback

Open or drop local media, then use the queue, seek bar, volume, speed and bookmarks. F11 enters/exits video fullscreen; Space toggles playback and the arrow keys seek. Fullscreen keeps the same native player and A/V clock.

**More → Audio tracks** and **More → Caption tracks** use libVLC's actual track list. Audio and captions can be turned off explicitly. The Captions button adds an external subtitle file; caption timing controls adjust the native subtitle delay. Embedded and external captions are rendered by libVLC, including the styling supported by the selected runtime. Selecting a track does not remux the file or silently choose a different audio track after an error.

The breadth of formats comes from the separately installed runtime and its modules. DRM, malformed media, every codec profile, interactive disc menus and every output device are not guaranteed. **More → Playback diagnostics** shows local runtime/version, native decoded/displayed counters and track state. It does not upload the report or media. WPF UI snapshots do not capture the native video child; native libVLC snapshots and displayed-picture counters are separate evidence.

## Share the current playlist locally

Sharing is off on every launch. Click **Share playlist → Start sharing** to show the local URLs, a random six-digit pairing code, expiry, paired-device count and discovery status. In Luma for Android, discover this computer or enter a displayed URL and pair with the code. The phone plays the original streamed file; desktop playback is not controlled remotely.

Use a trusted home network. The protocol uses local HTTP, so clients can buffer the media they are authorized to read. This computer must remain awake with sharing enabled. No cloud account, router port forwarding, upload, deletion or remote filesystem browser is added.

- Codes expire after ten minutes; existing paired tokens remain valid until sharing stops.
- **Stop and revoke** closes active streams and revokes all tokens. Closing the desktop app also stops sharing. Closing just the sharing details window leaves sharing active, indicated by **Sharing on** in the main toolbar.
- The shared list follows the live queue, including updated resume metadata. Removed/missing files are unavailable; removing an actively shared item also closes its stream.
- At most the first 1,000 items are exposed, with explicit truncation/total-count metadata. Titles are bounded to 512 UTF-16 units without broken surrogate pairs; times and JSON are bounded across clients.
- Windows attempts Bonjour/DNS-SD registration for `_luma-player._tcp.` using the built-in Windows API. Manual URLs work when multicast discovery is unavailable. An API registration result is not proof of discovery across a firewall or another network.

The wire contract is [Networking/PROTOCOL.md](../Networking/PROTOCOL.md). Authentication, private-source checks, single byte ranges, request limits, connection limits, cancellation and revocation have portable real-listener tests.

## Build and validate

Use PowerShell 7 on a Windows x64 desktop with the pinned [.NET SDK 10.0.404](https://dotnet.microsoft.com/download/dotnet/10.0). With a separately installed runtime, run:

```powershell
pwsh -File .\Windows\build.ps1 -SmokeTimeoutSeconds 600
```

For **explicit development/CI use only**, acquire the official pinned stock runtime into a separate ignored cache:

```powershell
pwsh -File .\Windows\build.ps1 -AcquireTestVlc -SmokeTimeoutSeconds 600
```

That flag verifies the VLC 3.0.24 archive SHA-256 before extraction, sets the process runtime path and records `libvlc-runtime-gate.json`. It never copies the runtime/plugins into the publish folder or ZIP. The script rejects such DLLs/plugins in the current review artifact. Runtime binaries are not installed automatically by the app.

The script runs the original portable core suites plus sharing tests, publishes the WPF app, and runs its native smoke mode. The new smoke requires native displayed-picture counters, snapshots, seeking, raw `hev1` Main10/AAC 5.1 decode, caption selection, lifecycle/resume, fullscreen and opt-in sharing UI checks. Native Windows results must be read from the matching CI run; cross-compilation alone cannot validate these operations.

When Core Audio reports zero active output endpoints, only the explicit smoke path requests `--aout=adummy`. Actual AAC decoding is still checked, while physical sound is recorded as unverified. Production playback uses normal output. Neither a decoded-audio counter nor a hosted CI run establishes sound at a speaker or audible A/V synchronization.

Outputs are under `Windows/artifacts/`. The script replaces its own regenerable outputs and does not create tags, upload, publish, sign or configure a release feed. Microsoft runtime notices are retained with self-contained builds.

For Android emulator interoperability, the portable test executable supports:

```text
dotnet Windows/Luma.Core.Tests/bin/Debug/net10.0/Luma.Core.Tests.dll --sharing-test-host <fixturesRoot> <readyJsonPath>
```

It exposes only the generated Demo and HEVC/AAC fixtures, writes private ready JSON with port/code/opaque IDs, and stops on stdin EOF or its test lifetime limit. Keep stdin open while the client runs. Do not publish the pairing-ready file in CI logs or artifacts.

## Saved data and updates

The existing local library location and schema are preserved. The old original decoders, demuxers and remux tests remain in `Luma.Core` as independent reference work; they are not the Windows GUI's active playback backend. See [FORMAT_SUPPORT.md](FORMAT_SUPPORT.md) and [CodecSupport.json](CodecSupport.json) for that distinction.

Windows retains its separate ECDSA-P256/SHA-256 updater. Its initial feed and key are blank, so no live Windows update service is claimed. It must not consume the Mac Ed25519/DMG feed. Review folders are replaced manually after quitting Luma. Signed publishing, installer distribution and a bundled runtime require separate owner review before production release.
