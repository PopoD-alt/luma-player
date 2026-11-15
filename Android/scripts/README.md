# Android native runtime build work

These original build/audit helpers operate in ignored `ThirdParty/work/android-lgpl`. They do not authorize redistribution or change the app's default development AAR. Select an approved staged package explicitly with `-PlumaRuntimePackage=android-lgpl-arm64-3.7.7-r2`; the root registry and Gradle release audit gate remain authoritative.

`build-lgpl-runtime.py prepare` verifies the pinned libvlcjni0b8dc65e and VLC84177b22 archives, extracts isolated copies, applies the exact20JNI upstream patches, and creates a recorded derivative build script. `build` additionally invokes the actual NDK29.0.14206865 arm64/API21 release compiler, with the upstream LGPL2.1/noGNUv3 contrib profile. Current local tool defaults target an Apple Silicon Mac and project-local Android SDK/JDK; prerequisite SDK/NDK licenses must be accepted using Android's tools. No production signing material is involved.

The derivative recipe fixes the upstream Linux-only build tuple using config.guess and disables Lua/stream-output services. Before static linking, `filter-native-modules.py` reads the actual generated Android module SOURCES, recursively inspects quoted local headers, and excludes the upstream blacklist plus GPL-marked, unknown-license, or unmapped modules. It records source/archive hashes and selection reasons. Run `python3 Android/scripts/test-native-filter.py` for independent selection checks.

The module filter supplements upstream metadata; it is not complete license clearance. Final core and contributed dependency compilation inputs, FFmpeg's GPL/NONFREE/VERSION3 flags, exact sources/patches/notices, ELF metadata and linkage, replacement/relink procedure, actual native playback, and a reproducible source bundle still require audit. Broad format support can change when GPL components are excluded; test the resulting runtime rather than assuming stock VLC parity.

The stock Maven AAR stays development-only until independently approved. This build never silently relabels or bundles it as a proprietary-compatible runtime.

The five original Python build/filter/test/source-AAR/replacement helpers in this directory are MIT licensed under [the build-support permission](../../ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt). This permission covers those build helpers only; the app retains its separate terms, and upstream source/patch licenses remain unchanged.

`build-source-aar.py` compiles the exact reviewed Java binding sources/resources, excludes the GPL renderer-discovery source and optional Lua/HRTF assets, and packages only the frozen ARM64 native libraries. It uses the pinned Android wrapper/plugin/dependencies and strict dependency verification. `prepare-native-review.py` now consumes that source-built AAR, rejects any base APK DEX containing the excluded class, and demonstrates independently signed debug APK replacement. It requires no stock AAR bytecode. Both helpers are MIT licensed.
