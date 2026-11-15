# Luma for Android — local streaming preview

Native Android companion by **sdas**. Pair with Luma on your desktop over the same trusted Wi-Fi, browse its live playlist, and stream media without saving a phone download. This preview uses official VideoLAN libVLC for playback; it does not recreate its codecs or guarantee every VLC format on every Android device.

**Development builds only:** the pinned libVLC AAR's complete native source, module licenses, and redistribution notices are still under audit in [ThirdParty/libvlc](../ThirdParty/libvlc/README.md). A Maven license label is not clearance for every bundled component. Do not publish an APK until the repository's third-party audit gate clears. The APK contains provisional audit/license texts in `assets/third-party`; these are not a complete distribution bundle.

## Use

1. Enable local playlist sharing in Luma on your desktop. Keep that computer awake and allow its local sharing port through the private-network firewall.
2. Connect the phone to the same trusted Wi-Fi. Allow local-network access when Android requests it.
3. Choose a nearby desktop, or enter the desktop's displayed `http://IP-address:port`.
4. Enter the current six-digit code from the desktop. Select an item from its live playlist.
5. Use play/pause, the seek slider, ±10 seconds, volume, and Full screen. Return to Playlist to choose another item.

The phone refreshes the playlist every five seconds while foregrounded. When the host bounds a large playlist, it clearly shows “Showing first N of M items” above the list. Protocol limits are 1000 returned items, 512 UTF-16 units per title, one year per duration/resume value, and 4 MiB per JSON response. Resume positions are shown from the desktop and kept in phone memory during the current session. Pausing/backgrounding stops network playback; returning can reopen the stream. Re-pair after desktop sharing restarts. The desktop URL is the only saved connection preference. Pairing tokens remain in memory and are scoped to that desktop's current sharing session.

Local sharing uses **unencrypted HTTP**. Use a home network you trust. Manual addresses must be numeric private/link-local/loopback IP addresses with an explicit port; public hosts, DNS names, credentials, cloud URLs, cross-origin stream links and redirects are rejected. Native libVLC receives a nonce-protected loopback URL; the desktop token stays in the bounded HTTP relay. The relay uses a 64 KiB transfer buffer, forwards GET/HEAD and single byte ranges, and writes no media files. No cloud service, analytics, upload, or remote desktop controls are implemented. Play trusted media from your own desktop.

## Build

Requires **JDK17**, the Android SDK, `platforms;android-37.0`, `build-tools;36.0.0`, and platform-tools. Set `ANDROID_HOME` or use an untracked `local.properties` with `sdk.dir`.

```sh
cd Android
./gradlew --no-daemon assembleDebug assembleDebugAndroidTest testDebugUnitTest lintDebug
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

The Gradle9.6.0 wrapper verifies the official distribution SHA256. AGP9.4.0 and all direct dependencies have pinned versions. `gradle/verification-metadata.xml` pins resolved dependency checksums, including the candidate AAR. Build tools and downloads under `.tools/` are local-only and excluded from git.

- Main development APK: `app/build/outputs/apk/debug/app-debug.apk`
- Test APK: `app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk`
- Debug application ID: `dev.sdas.luma.preview`
- Android8/API26 minimum; compile/target API37. Runtime ABIs: arm64-v8a, armeabi-v7a, x86_64.
- No production signing key or store listing is configured. Debug signing is for testing only.

On Android17/API37 the app requests `ACCESS_LOCAL_NETWORK`. Android16/API36 uses `NEARBY_WIFI_DEVICES` for the applicable local-network protection flow. Earlier versions need the manifest network permissions; no location access is requested. Discovery uses `_luma-player._tcp.` with a manual connection fallback. Routers that isolate Wi-Fi clients or suppress multicast can prevent discovery or streaming.

## Validation

`testDebugUnitTest` exercises private-address restrictions, same-origin/session-scoped stream URLs, actual HTTP pairing/Bearer playlist requests, malformed metadata, redirect denial, GET/HEAD/range forwarding and token-hiding relay URLs. `lintDebug` checks Android API usage and app resources.

The instrumentation test uses the **production desktop sharing server**, not a simulated playlist or fake decoder. Start its synthetic fixture host with stdin kept open:

```sh
dotnet Windows/Luma.Core.Tests/bin/Debug/net10.0/Luma.Core.Tests.dll \
  --sharing-test-host FIXTURES_DIRECTORY READY_JSON_PATH
```

Read its ready JSON (`port`, `code`, `ids.demo`, `ids.hevc10`) without printing credentials. Install both APKs, then substitute those values:

```sh
adb install -r app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
adb shell am instrument -w \
  -e serverUrl http://10.0.2.2:PORT -e pairCode CODE \
  -e demoId DEMO_ID -e hevcId HEVC_ID \
  dev.sdas.luma.preview.test/androidx.test.runner.AndroidJUnitRunner
```

API36 `google_apis` x86_64 is suitable for CI. API35 is also supported and needs no runtime local-network permission. The test pairs through the actual dialog, loads the live playlist, streams AVC and synthetic HEVC Main10/AAC5.1, and asserts native decoded video/audio plus displayed-frame counters. It also checks pause, byte-range seek, volume and fullscreen transitions. Status output includes libVLC version/changeset and decoder counters. It checks app files/cache for saved media downloads.

Successful emulator output is software decoding/rendering evidence, **not proof of audible physical speaker output, surround routing, physical Wi-Fi discovery, every hardware decoder, or every VLC codec/container**. Those require real-device coverage. The synthetic AAC5.1 fixture tests channel-layout decoding, not all real-world multichannel streams. Runtime CI results are recorded by the parent workflow; do not infer a passed emulator run from compilation alone.

## Dependency provenance

- Official candidate: `org.videolan.android:libvlc-all:3.7.7` — [Maven artifact](https://repo.maven.apache.org/maven2/org/videolan/android/libvlc-all/3.7.7/), [VideoLAN source project](https://code.videolan.org/videolan/libvlcjni).
- AAR SHA256: `b48dab96e0e90e34cce3861963500c144a5aadb1b249d501d6a4b104d849e61b`.
- Wrapper distribution SHA256: `bbaeb2fef8710818cf0e261201dab964c572f92b942812df0c3620d62a529a01`.
- Official toolchain references: [Android Gradle plugin](https://developer.android.com/build/releases/gradle-plugin), [Gradle distributions](https://services.gradle.org/distributions/), [Android SDK tools](https://developer.android.com/studio), [local-network permission](https://developer.android.com/privacy-and-security/local-network-permission).

The protocol is defined in [Networking/PROTOCOL.md](../Networking/PROTOCOL.md). Root release workflows own public artifact publication and audit gates.
