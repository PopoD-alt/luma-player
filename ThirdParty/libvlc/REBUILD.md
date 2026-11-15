# Rebuilding curated libVLC components

## macOS arm64

This is a technical reproduction recipe, not legal or patent clearance. The
stock VLCKit framework is not the curated runtime. Do not copy its static binary
or the unfiltered install directory into an application.

Requirements: an Apple Silicon Mac, macOS Command Line Tools with a macOS SDK,
Python 3.11 or later, Git, Make, a C/C++ compiler and a POSIX shell. No full Xcode
application is needed for the direct C library build. The first tools build may
download the separately distributed build tools from the source-pinned recipes.
The demonstrated build used the Command Line Tools macOS 27.2 SDK, explicitly
targeting macOS 14. Runtime support on older macOS releases is not claimed.

When starting with the embedded `CorrespondingSource.tar`, extract it into an
empty working directory. Its `upstream/` and `contrib/` directories contain the
original archives, not altered substitutes. The full patched source is recreated
by the scripts, preserving the original 47 patch files in the VLCKit snapshot.
From that directory:

```sh
mkdir -p ThirdParty/downloads ThirdParty/libvlc
cp upstream/* ThirdParty/downloads/
cp -R audit/. ThirdParty/libvlc/
python3 Scripts/prepare-libvlc-sources.py setup
mkdir -p ThirdParty/work/vlc-79128878/contrib/tarballs
cp contrib/* ThirdParty/work/vlc-79128878/contrib/tarballs/
bash Scripts/prepare-libvlc-macos.sh tools
python3 Scripts/prepare-libvlc-sources.py fetch-contrib
bash Scripts/prepare-libvlc-macos.sh contrib-reset
bash Scripts/prepare-libvlc-macos.sh core-reset
python3 Scripts/prepare-libvlc-macos-audit.py
```

`--disable-gpl --disable-gnuv3 --enable-ad-clauses` constrain contributed
dependencies. They do not constrain VLC's own modules. The final command expands
the generated plugin and linked helper-library source lists, recursively
inventories quoted source headers and excludes GPL-restricted or uncertain
modules even when their default entry
metadata says LGPL. The final curated runtime is produced with:

```sh
python3 Scripts/prepare-libvlc-macos-audit.py \
  --stage ThirdParty/runtime/rebuilt-review \
  --source-bundle /path/to/CorrespondingSource.tar
```

This rewrites only internal library install names, checks arm64 and minimum-OS
load commands, rejects unknown dynamic dependencies, signs the modified dylibs
ad hoc, preserves the ABI aliases, copies upstream notices and embeds the exact
source archive. The temporary unfiltered `work/...-install` remains a development
intermediate. Runtime plugin discovery derives from the loaded core's location;
it does not need that install path or a separately installed VLC application.
Some upstream TS helpers have an explicit LGPL2.1+ grant followed by a stale GPL
receipt sentence. The audit records each exact source hash and this narrow
clarification; it does not treat that notification as a GPL grant. Mixed or
uncertain implementations remain excluded.

The build deliberately prevents Git from finding the enclosing Luma repository;
its runtime changeset is `Luma-LGPL-79128878-VLCKit319ed2c0-p47`. It also gives
libtool a conservative, checked command-length limit when a restricted shell
denies the Darwin sysctl probe. Generated binaries are not claimed to be
bit-for-bit reproducible: toolchain versions, SDK, timestamps and ad-hoc signing
can change their hashes. Reproduction of source selection and flags is distinct
from approval of the resulting bytes.

Run native playback/seek/audio/subtitle checks against the new root using
`LUMA_LIBVLC_PATH=/absolute/runtime/lib/libvlc.5.dylib`. Never reuse the old
artifact's central hash/approval for a rebuilt runtime. Its complete file
inventory, source archive, module audit, dependencies and actual playback need
review before registering the new artifact for distribution.

User replacements are permitted: the app loader accepts an explicit absolute
`LUMA_LIBVLC_PATH` pointing to a compatible libVLC 3 build. Luma's packaging hash
gate must not be applied to a user's replacement runtime. On macOS, modified app
bundles may need local ad-hoc re-signing; this does not grant rights beyond the
app's license and the applicable third-party licenses.

## Windows x64

The Windows component is built from VLC commit
`6de05adcbaf2e8b85fe86aad4169393098628119` in the pinned Linux amd64
VideoLAN build image. The helper downloads a hash-pinned LLVM-MinGW toolchain,
verifies the exact VLC archive and contributed-source recipe checksums, builds
the DLLs, audits their source/module/import selection and stages the curated
component. It does not build a Luma application, approve the resulting bytes or
publish anything.

From a Luma checkout, with Docker available:

```sh
docker run --rm --platform linux/amd64 \
  -v "$PWD:/luma" -w /luma \
  registry.videolan.org/vlc-debian-llvm-ucrt@sha256:a0f9255792ee12da494efebdd203e8d11d769b7d938d5191920f76754d29f6e1 \
  bash Scripts/prepare-libvlc-windows.sh \
    /luma/ThirdParty/work/windows-lgpl-rebuild \
    /luma/ThirdParty/work/windows-lgpl-rebuild-output
```

Use new absolute, space-free work/output paths. The helper preserves toolchain
and configuration evidence, including the narrow upstream-equivalent libtool
macro correction that retains Clang's compiler-runtime archive in C++ DLL
links. Large-stack C/C++ direct and libtool link probes run before media
dependencies are built. These probes do not execute Windows code.

The contributed-library build keeps `--disable-gpl --disable-gnuv3
--disable-sout`; all FFmpeg encoder components must remain disabled. The VLC
core separately enables its upstream image-encoder framework so PNG snapshots
work. That framework and `modules/codec/png.c` have LGPL2.1+ grants. The audit
records the actual core/FFmpeg configuration and requires the PNG source
closure. Curated output excludes `access_output`, `mux`, `stream_out` and the
internal stream-recording module, in addition to every GPL-restricted or
unclassified module. This does not add a Luma transcoder or streaming-output UI.
Luma's own HTTP playlist server serves original files and does not need those
libVLC output modules.

The staged component carries `Sources/CorrespondingSource.tar`, complete
source/notices inventories, upstream acknowledgements and `Audit/BuildInputs`.
The source archive contains the exact VLC archive, all contributed-library
source archives and original patches/recipes, matching build/audit helpers,
configuration snapshots and the recorded local build-tool correction. Starting
with that source archive, extract it into an empty directory and use the same
container command above. Its helpers are sufficient for a component build;
building the Luma interface additionally requires the matching application
source checkout and the Windows/.NET build described in `Windows/README.md`.

To reuse the supplied source archives rather than download them again, seed
the new work directory before running the builder: copy `upstream/vlc-6de05adcba-research.tar.gz`
into its `downloads/`, unpack that verified archive at the work directory root
(beside `downloads/`), and write its SHA256 alone to `source-input.sha256` at
that root. Copy the supplied `contrib/` archives to
`vlc-6de05adcba/contrib/tarballs/` within that work directory. The helper still
verifies the pinned VLC hash and every selected contributed-source checksum.
Build tools and the pinned compiler may require network access.

Source rebuilds are not claimed to produce identical native bytes. A rebuilt
component remains development-only until its complete inventory, source and
license profile, native Windows playback/snapshots and replacement route have
been independently reviewed. Never copy the unfiltered install tree into an
app or reuse an older runtime's approval for different bytes. Physical speaker
output, every Windows/device version and universal media compatibility are
separate from these checks.

For an exact inventoried CI component, the transport archive can be recreated
without rebuilding or altering any DLL, notice or embedded source byte:

```sh
python3 Scripts/prepare-libvlc-windows-archive.py \
  --component /absolute/path/to/raw-ci-component \
  --output /absolute/new/path/windows-lgpl-x64-3.0.24-r1-runtime.tar
```

The helper verifies the original development manifest and rejects changed,
missing, extra, linked or case-colliding files. It emits sorted regular-file
PAX entries with fixed permissions, zero timestamps/IDs and no host names,
excluding only that original root manifest. This creates transport bytes, not
approval. Only the central `prepare-libvlc.py` gate can prepare a distributable
stage using an independently reviewed registry record and the exact archive,
inventory and source hashes.

Windows user replacements remain separate from that packaging gate. Set
`LUMA_LIBVLC_PATH` to the absolute path of a compatible x64 `libvlc.dll`, with
its matching `libvlccore.dll` and `plugins/` alongside it. The application checks
the libVLC 3 ABI and loads that explicit user selection before its pinned
app-local runtime. It does not apply the publisher's component hash to a user's
replacement. Modified libraries retain their own applicable license obligations.
