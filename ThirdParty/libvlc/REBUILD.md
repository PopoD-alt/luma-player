# Rebuilding the curated macOS arm64 component

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
