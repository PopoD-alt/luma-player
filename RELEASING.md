# Releasing updates

The source and downloads belong to `PopoD-alt/luma-player`. Official downloads
are free; the source retains the proprietary LICENSE. The first alpha is an
explicitly unnotarized preview for macOS 14+ on Apple Silicon.

## Owner-tested 0.7.0 preview

The owner reported that the tested version works as expected; the exact
device/platform matrix was not supplied. `v0.7.0-preview.1` packages those reviewed binaries without rebuilding
their playback code: an unnotarized/ad-hoc Mac DMG, unsigned Windows portable ZIP
and debug-signed Android ARM64 APK with a complete matching source/license
companion. The Android app's internal version is 0.1.0-preview.

Preserve the immutable owner-review folders. Package from copies, retain complete
library sources/notices and application inputs, and verify binary/native hashes
against the reviewed copies. Document any package-only instruction/provenance
changes and generate an accurate public file inventory. Verify the DMG and its
mounted signature, ZIP contents, APK signature, source companions and asset
checksums before publishing the GitHub prerelease.

This preview publication does not replace `Updates/manifest.json`, generate new
trust keys, or configure Windows/Android automatic updates. Keep the existing
stable feed at 0.6.1 until a deliberate stable update is published through the
signed process below. The current x64 emulator Wi-Fi failure remains recorded;
owner feedback is not a replacement for the separate CI evidence/device matrix.

## Signing keys and configuration

`ReleaseConfig.json` contains public settings only: repository, stable HTTPS
manifest URL and Ed25519 verification key. The private update-signing key is
stored in the publisher's macOS login Keychain under the Luma release-signing
service. Do not export it into Git, send it in chat or replace it for each
release. Installed apps trust the existing public key. Preserve access to the
signing Mac and arrange secure Keychain recovery before depending on it.

## Publish an unnotarized alpha update

1. Increase both the numeric app version and build in `Info.plist`. Every update
   must have a build number higher than the installed version.
2. Update `RELEASE_NOTES.md`, `README.md`, `ROADMAP.md` and `CodecSupport.json` to
   describe actual shipped behavior and remaining limits.
3. Commit the changes and review that exact commit. The working tree must be
   clean before and after the release checks.
4. Run `python3 Scripts/release.py doctor`, then
   `python3 Scripts/release.py prepare-preview --reviewed-sha FULL_COMMIT_SHA --notes RELEASE_NOTES.md`.
   This builds, tests, ad-hoc signs and creates the clearly labelled preview
   DMG, with installation instructions and an Applications shortcut. It signs
   an update manifest for the final DMG bytes. It does not notarize or upload.
5. Create a GitHub prerelease tagged `vVERSION` at the reviewed source commit.
   Attach the generated DMG and `manifest.json` from `build/releases/BUILD/`.
   Keep the versioned asset URL exactly as written in the signed manifest.
6. Verify the public DMG is downloadable and its size/SHA-256 match the signed
   manifest. Then copy that same `manifest.json` to `Updates/manifest.json` on
   `main`, commit and push it. Publishing the feed last prevents clients from
   being offered an asset that is not available yet.
7. Test the update prompt and manual installation on another compatible Mac.

Installed apps check the configured feed when due on launch and daily while
active. A new signed build prompts only when playback/fullscreen are idle.
Users download a verified DMG and replace the app manually; saved library data
stays outside the bundle. A Git push without a published DMG and feed update
does not release an app update. There is no background development worker.

## Apple-signed releases later

`python3 Scripts/release.py prepare --reviewed-sha FULL_COMMIT_SHA --notes RELEASE_NOTES.md --identity 'Developer ID Application: NAME (TEAM)' --notary-profile PROFILE`
keeps the production path separate. It requires a valid Developer ID identity
and Keychain notarization profile, notarizes/staples the app and DMG, verifies
the disk image, and hashes the final stapled bytes for the signed manifest.
Preview preparation never silently substitutes for failed notarization.

The manual GitHub Actions workflow uses a trusted, self-hosted signing Mac and
an explicit production-review environment. It prepares artifacts; publication
remains a separate owner action. No account passwords, access tokens, private
keys or signing certificates belong in the source or release attachments.
