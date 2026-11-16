# Luma Player for Windows

Luma keeps its native WPF interface, queue, bookmarks, resume positions, themes and fullscreen controls. Playback now goes directly to **libVLC 3** for local media. The app no longer makes a whole-file Matroska remux or an HEVC compatibility copy before playing a file, and that former 512 MiB gate does not apply to playback.

The public download is **0.7.0 Windows preview build 4**, targeting Windows 11 x64 and .NET 10. The owner tested the supplied copies and reported that they work as expected. Windows on ARM, other Windows versions, every codec/profile and every physical output device are outside that general confirmation. No installer or production-signed Windows build is included.

## Auto-skip preview

The current source on `main`, updated **2026-11-16**, builds **0.10.0 review build 7**; the public download remains 0.7.0. Enable the **Auto-skip** checkbox below the video. It starts off. Named intro/opening and credits/ending chapters supply boundaries, and the adjacent Skip button permits a manual jump. **Skip setup** marks exact start/end positions or clears saved ranges when a file lacks useful chapter names.

**More → Analyze openings / credits with next queue item…** compares the current item with the next local episode. It scans bounded first/last-three-minute audio windows using the current playback runtime, shows progress and supports cancellation. Allow up to about 12 minutes for long files. Use **More → Review detected matches** to preview the proposed range, then Save it explicitly for the current video. Closing the review retains unconfirmed results while that input remains open. Pause before a long analysis; changing videos or automatic queue advance cancels it. Confirm a suggested range before it is saved for the current video; no analysis result enables auto-skip automatically. Audio can repeat in story scenes too, so review the actual boundary. Files remain local and unchanged; no AI model or cloud service is used.

[0.10.0 native Windows verification](https://github.com/PopoD-alt/luma-player/actions/runs/43954997802), on source `bf03b1d73216e498c437b683eb30941f1f388949`, passes startup Continue Watching, authenticated phone-to-normal-Play clock/frame handoff, the retained paused native player, one-shot manual seek priority, unchanged playback on incoming progress and explicit zero restart. It also passes the existing chapter/auto-skip, post-credit preservation, keyboard, repeated audio/review, format and paired sharing checks. The run uses the approved app-local runtime. Its host has no audio output endpoint, so physical audibility remains unverified. Cross-built local EXE bytes are separate from this native source/runtime validation.

The library persists the switch and per-file ranges. Saved desktop ranges are shared read-only with Android 0.3.0 and later. Explicit seeks and pauses keep user intent, and a separate post-credit chapter remains playable. A generic chapter name or an unlabelled post-credit scene cannot provide a reliable skip boundary by itself.

## Device progress review

The 0.10.0 source on `main` keeps independent Computer/Phone progress bars, times, percentages and completion state. When Luma opens, **Continue Watching** shows the most recently played item across the saved computer and phone checkpoints, its last device/time, and a **Resume** or **Play again** button. Startup never opens media automatically. Selecting a different queue item while idle shows its card; double-click it or press Play to start.

Opening an item uses its newest valid computer or phone checkpoint. A newer known phone position of zero starts at the beginning; a completed checkpoint offers replay from the beginning. Equal device timestamps prefer the computer. Libraries without timestamps keep their existing local resume bookmark.

To switch back from a phone while the video remains paused on Windows, press **Play** or **Space**. If the phone has a newer checkpoint, Luma seeks the retained native player to that position and plays. **Resume from phone** remains an explicit override. A manual paused seek, bookmark jump, previous/restart action or manual Skip preserves that requested position for the next Play, even if another phone checkpoint arrives; this priority is consumed once.

An arriving phone checkpoint never seeks or replaces an already loaded player, including while paused. During idle browsing, newer phone activity can update Continue Watching unless you explicitly selected another item. It does not change an actively playing video.

Enable playlist sharing and use an Android build that reports progress to receive phone checkpoints, normally every five seconds while the phone plays. Checkpoints are stored in this computer's local library; multiple paired phones share one Phone position per item. The computer must be awake and sharing to receive a new phone position. No cloud service or simultaneous playback synchronization is involved. Older phone clients can keep streaming without reporting progress. Public downloads below remain 0.7.0; the review behavior requires matching review builds.

## Download and run

1. Download [Luma-0.7.0-Windows-x64-preview.zip](https://github.com/PopoD-alt/luma-player/releases/download/v0.7.0-preview.1/Luma-0.7.0-Windows-x64-preview.zip).
2. Extract the entire ZIP to a normal writable folder.
3. Open **Luma-Player-Windows-x64**, then run **Luma Player.exe**.

The public preview includes the reviewed app-local libVLC runtime and the .NET runtime. **No separate VLC or .NET installation is needed.** Keep the whole folder together, including `libvlc/`, sources and notices. Do not run the EXE inside the ZIP. The app is unsigned and portable, without a setup wizard or uninstaller. See [INSTALL.md](../INSTALL.md#windows).

The EXE was cross-built on macOS. Matching Windows application source and the approved app-local component passed [native Windows CI](https://github.com/PopoD-alt/luma-player/actions/runs/43765447494); the exact downloadable EXE bytes were not separately run in the automated Windows validation recorded here. The owner confirmation does not supply a detailed device matrix.

## External runtime for development or personal replacement

For a development build without the approved app-local component, install the official **64-bit VLC 3** application separately, or set `LUMA_LIBVLC_PATH` to an absolute path to its `libvlc.dll`. A compatible personal runtime replacement can also use that explicit override. The matching `libvlccore.dll` and `plugins` folder must remain beside it. For example:

```powershell
$env:LUMA_LIBVLC_PATH = 'C:\Program Files\VideoLAN\VLC\libvlc.dll'
& '.\Luma Player.exe'
```

The adapter checks the libVLC major version. The shipped `<app>/libvlc/` component has exact package, manifest and source pins. Stock VLC packages remain development-only; a separately installed runtime used by tests is not permission to redistribute it. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and the bundled library's source/notices.

Keep all files from the Luma review folder together. The Windows review executable is unsigned and has no established trusted publisher reputation. Use the provenance and your local policy to decide whether to run it; do not disable system protection globally.

## Playback

Open or drop local media, then use the queue, seek bar, volume, speed and bookmarks. F11 enters/exits video fullscreen; Space toggles playback and the arrow keys seek. Fullscreen keeps the same native player and A/V clock.

**More → Audio tracks** and **More → Caption tracks** use libVLC's actual track list. Audio and captions can be turned off explicitly. The Captions button adds an external subtitle file; caption timing controls adjust the native subtitle delay. Embedded and external captions are rendered by libVLC, including the styling supported by the selected runtime. Selecting a track does not remux the file or silently choose a different audio track after an error.

The breadth of formats comes from the selected runtime and its modules. DRM, malformed media, every codec profile, interactive disc menus and every output device are not guaranteed. **More → Playback diagnostics** shows local runtime/version, native decoded/displayed counters and track state. It does not upload the report or media. WPF UI snapshots do not capture the native video child; native libVLC snapshots and displayed-picture counters are separate evidence.

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

An explicitly approved custom component can instead be included in a **local owner-review folder**:

```powershell
pwsh -File .\Windows\Test-BundleBuild.ps1
pwsh -File .\Windows\build.ps1 -BundledRuntimeRoot 'C:\LumaReview\approved-component' -SkipReviewZip -SmokeTimeoutSeconds 600
```

The input must be a final centrally prepared component that passes `Scripts/prepare-libvlc.py verify --distribution`. A raw development artifact or a locally edited approval flag is insufficient. This option verifies approval before replacing old outputs, embeds the exact package/manifest/source pins, copies the entire approved component into `Windows/artifacts/win-x64/libvlc`, and verifies the copied tree before and after native playback. The native test clears inherited runtime overrides and requires the app-local route. The folder retains the component's source companion and notices; keep it complete when moving it. `-SkipReviewZip` creates no review archive or installer. A compatible personal replacement remains available through `LUMA_LIBVLC_PATH` during normal use.

This is an opt-in build path. Its existence does not approve a component, publish an app or establish a native Windows result for bytes that have not passed the separate review.

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

Windows retains its separate ECDSA-P256/SHA-256 updater. Its initial feed and key are blank, so no live Windows update service is claimed. It must not consume the Mac Ed25519/DMG feed. Quit Luma and replace the complete portable folder to update this preview. Production signing, an installer and a public Windows update feed remain future distribution work; the reviewed runtime is already included in this preview.
