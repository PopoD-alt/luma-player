#!/bin/bash
# Original local build driver; upstream sources and patches retain their own licenses.
# This creates development build inputs only. It never approves or packages an app.
set -euo pipefail
cd "$(dirname "$0")/.."
LUMA_REPO="$PWD"
LUMA_VLCROOT="$LUMA_REPO/ThirdParty/work/vlc-79128878"
LUMA_TOOLS="$LUMA_VLCROOT/extras/tools/build/bin"
LUMA_CONTRIB="$LUMA_VLCROOT/contrib/luma-macos-arm64"
LUMA_CORE="$LUMA_VLCROOT/build-luma-lgpl-arm64"
LUMA_PREFIX="$LUMA_REPO/ThirdParty/work/lgpl-macos-arm64-install"
LUMA_JOBS="${LUMA_LIBVLC_JOBS:-8}"
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || { echo 'This build driver requires an Apple Silicon Mac.' >&2; exit 2; }
[[ "$LUMA_JOBS" =~ ^[1-9][0-9]?$ ]] || { echo 'LUMA_LIBVLC_JOBS must be1–99.' >&2; exit 2; }
python3 Scripts/prepare-libvlc-sources.py setup
case "${1:-}" in
  tools)
    cd "$LUMA_VLCROOT/extras/tools"
    ./bootstrap
    make -j"$LUMA_JOBS"
    ;;
  contrib)
    [[ -x "$LUMA_TOOLS/pkg-config" && -x "$LUMA_TOOLS/cmake" && -x "$LUMA_TOOLS/meson" ]] || { echo 'Build tools first.' >&2; exit 2; }
    export PATH="$LUMA_TOOLS:$PATH"
    # The source-inventory bootstrap is the exact upstream noGPL/noGNUv3 profile.
    # Host libraries were excluded while fetching, so every input archive is available.
    [[ -f "$LUMA_CONTRIB/Makefile" ]] || python3 Scripts/prepare-libvlc-sources.py fetch-contrib
    if grep -Eq '^(GPL|GNUV3)[[:space:]]*:?=[[:space:]]*1' "$LUMA_CONTRIB/Makefile"; then
      echo 'Refusing a GPL/GNUv3 dependency configuration.' >&2; exit 2
    fi
    cd "$LUMA_CONTRIB"
    # Some launch environments export DEBUG=release; ancient GSM interprets DEBUG
    # as literal compiler flags. Keep this build variable empty, and give the
    # Darwin version helper an actual SDK path rather than an empty --sdk option.
    make -j"$LUMA_JOBS" USE_FFMPEG=1 PKG_CONFIG="$LUMA_TOOLS/pkg-config" \
      SDKROOT="$(xcrun --sdk macosx --show-sdk-path)" DEBUG= \
      CC=clang CXX=clang++ AR=ar RANLIB=ranlib NM=nm STRIP=strip
    ;;
  core)
    [[ -x "$LUMA_TOOLS/autoreconf" && -f "$LUMA_CONTRIB/install/lib/pkgconfig/libavcodec.pc" ]] || { echo 'Build tools and contributed libraries first.' >&2; exit 2; }
    export PATH="$LUMA_TOOLS:$PATH"
    cd "$LUMA_VLCROOT"
    ./bootstrap
    mkdir -p "$LUMA_CORE"
    cd "$LUMA_CORE"
    # Dynamic plugins can be individually audited/excluded. A static framework with
    # GPL plugins cannot be repaired by changing a search path or its license label.
    ../configure --build=aarch64-apple-darwin --host=aarch64-apple-darwin \
      --prefix="$LUMA_PREFIX" --with-contrib="$LUMA_CONTRIB/install" \
      --enable-shared --disable-static --disable-vlc --disable-macosx \
      --disable-qt --disable-skins2 --disable-lua --disable-nls --disable-dbus \
      --disable-vlm --disable-sout --disable-update-check --disable-notify \
      --disable-dvdread --disable-dvdnav --disable-bluray --disable-vcd \
      --disable-addonmanagermodules --disable-sparkle --disable-mad --disable-a52 \
      CC=clang CXX=clang++ OBJC=clang \
      CFLAGS='-O2 -arch arm64 -mmacosx-version-min=14.0' \
      CXXFLAGS='-O2 -arch arm64 -mmacosx-version-min=14.0' \
      OBJCFLAGS='-O2 -arch arm64 -mmacosx-version-min=14.0' \
      LDFLAGS='-arch arm64 -mmacosx-version-min=14.0'
    make -j"$LUMA_JOBS"
    make install
    echo 'Development install only. GPL-plugin/source/license/dependency audit must pass before distribution.'
    ;;
  *) echo "Usage: $0 tools|contrib|core" >&2; exit 2 ;;
esac
