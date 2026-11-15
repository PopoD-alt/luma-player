# libVLC preparation and audit

Run from the repository root:

```sh
python3 Scripts/prepare-libvlc.py status
python3 Scripts/prepare-libvlc.py prepare --package macos-vlckit-3.7.3 --development
python3 Scripts/prepare-libvlc.py verify --runtime ThirdParty/runtime/macos-vlckit-3.7.3
python3 Scripts/prepare-libvlc.py verify --runtime ThirdParty/runtime/macos-vlckit-3.7.3 --distribution
python3 Scripts/prepare-libvlc-tests.py
```

The stock-candidate distribution verification fails deliberately. A successful development
preparation prints JSON with `runtimeRoot`, `runtimeEntry`, `manifestPath`,
`developmentOnly: true` and `redistributionApproved: false`. `--runtime` chooses
a different staging directory; an existing directory must match its complete
recorded inventory. Preparation never upgrades an incomplete audit or falls back
to a stock VLC installation. Only an exact, separately recorded custom runtime
can pass all registry gates. Do not bypass this gate in release packaging.

The runtime loader should remain able to accept a user's compatible modified
library. This preparation check protects Luma's reviewed distribution inputs; it
must not become a runtime signature/hash restriction on LGPL replacement.

## Confirmed macOS findings

The official VLCKit 3.7.3 archive has universal arm64/x86_64 code and exports the
libVLC C API. Actual local `dlopen`, version/changeset queries, creation/release
and Luma production playback tests succeeded. These are runtime evidence, not
license clearance.

`macos-runtime-modules.txt` was captured by calling that exact library's
`libvlc_new` with `--ignore-config --list-verbose`; no media was opened. The list
includes `syslog`, `mono`, `rotate` and the dummy interface. In the exact patched
VLC source, these implementation files explicitly declare
`VLC_MODULE_LICENSE_GPL_2_PLUS`:

- `modules/logger/syslog.c`
- `modules/audio_filter/channel_mixer/mono.c`
- `modules/video_filter/rotate.c`
- `modules/control/dummy.c`

The framework links these modules statically within its shared binary. Removing
a plugin search path or refusing to select these modules does not remove their
code from that binary. Do not attempt to turn it into an LGPL-only artifact by
renaming it or removing its notice. The current proprietary route requires a
rebuild with a reviewed module allowlist. `macos-module-source-map.json` is a
heuristic aid, not an authoritative license verdict: aliases such as `macosx`
can refer to the LGPL video output rather than the GPL application interface.

## Source inputs

Pinned upstream VLCKit `319ed2c0` references VLC `79128878` and contains 47
patches. The core version is 3.0.23, consistent with the actual runtime changeset
`3.0.23-2-47-g4eb3701ba9`. Both source archives were acquired and SHA256-pinned.
All 45 source archives selected by the patched upstream macOS contrib profile
were downloaded and checked against the recipe's SHA512 values. See
`macos-contrib-config.json` and `macos-source-inventory.json`. Host library
substitution was disabled for this source inventory, so the archive set is
conservative rather than omitting a dependency because it exists on this Mac.
License/notice paths within every dependency archive are recorded in that
inventory. Obtaining sources does not change the stock binary's GPL contents.

The original framework was built with Xcode 13.4.1 and macOS SDK 12.3 according
to its embedded configuration. This machine has Command Line Tools only, so the
upstream VLCKit Xcode-project build cannot currently run here. A direct shared
libVLC/core/plugin build succeeded using Command Line Tools; local build tools
and dependencies are built under the ignored `ThirdParty/work` tree. No machine-wide build tools or
VLC application have been installed by this workflow.

## Original curated macOS build

See [REBUILD.md](REBUILD.md) for the source archive layout and full recipe.
`prepare-libvlc-macos.sh` builds the pinned tools, GPL-disabled contributed
dependencies and shared core/plugins for arm64 with a macOS14deployment target.
All61static archives from the actual rebuild report14.0. The final FFmpeg
configuration disables GPL/nonfree/version3/GPLv3, and its1460generated dependency
files select none of111GPL-marked source/header paths. These are technical build
checks, not legal clearance or proof on every macOS version.

`prepare-libvlc-macos-audit.py` expands plugin sources and linked convenience
library sources, inventories quoted headers and excludes GPL-restricted or
uncertain modules. Exact LGPL2.1+ grants with a stale GPL receipt sentence are
recorded separately. It rewrites internal dylib dependencies to loader-relative
paths, rejects unknown libraries, validates deployment targets and signs local
dylibs ad hoc. Only selected dylibs are staged; the unfiltered install/bin tree
is never an application dependency.

The curated root embeds full notices and `Sources/CorrespondingSource.tar`.
Preparation binds every binary, plugin, notice and source archive to the central
approved inventory. This packaging gate does not restrict a user's compatible
replacement runtime. Rebuilding does not automatically approve new hashes;
native application playback and replacement checks remain required.

Full downloaded binaries/sources and build intermediates are ignored under
`downloads/`, `runtime/` and `work/`; release source archives must be separately
retained and made available with any eventual approved distribution. Source
archives, patch sets and notices must match that final build, not merely a nearby
upstream version. The Android sources JAR is Java-only and cannot stand in for
its native VLC/JNI/contrib sources.
