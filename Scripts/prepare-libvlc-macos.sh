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
  contrib|contrib-reset)
    [[ -x "$LUMA_TOOLS/pkg-config" && -x "$LUMA_TOOLS/cmake" && -x "$LUMA_TOOLS/meson" ]] || { echo 'Build tools first.' >&2; exit 2; }
    export PATH="$LUMA_TOOLS:$PATH"
    # The source-inventory bootstrap is the exact upstream noGPL/noGNUv3 profile.
    # Host libraries were excluded while fetching, so every input archive is available.
    [[ -f "$LUMA_CONTRIB/Makefile" ]] || python3 Scripts/prepare-libvlc-sources.py fetch-contrib
    if grep -Eq '^(GPL|GNUV3)[[:space:]]*:?=[[:space:]]*1' "$LUMA_CONTRIB/Makefile"; then
      echo 'Refusing a GPL/GNUv3 dependency configuration.' >&2; exit 2
    fi
    cd "$LUMA_CONTRIB"
    if [[ "${1:-}" == contrib-reset ]]; then
      # Rebuild only generated dependency outputs; preserve verified source tarballs.
      make mostlyclean
    fi
    cat > config.mak <<'CONFIG'
EXTRA_CFLAGS += -mmacosx-version-min=14.0 -arch arm64
EXTRA_LDFLAGS += -mmacosx-version-min=14.0 -arch arm64
CONFIG
    # Some launch environments export DEBUG=release; ancient GSM interprets DEBUG
    # as literal compiler flags. Keep this build variable empty, and give the
    # Darwin version helper an actual SDK path rather than an empty --sdk option.
    make -j"$LUMA_JOBS" USE_FFMPEG=1 PKG_CONFIG="$LUMA_TOOLS/pkg-config" \
      SDKROOT="$(xcrun --sdk macosx --show-sdk-path)" DEBUG= \
      MACOSX_DEPLOYMENT_TARGET=14.0 VLC_DEPLOYMENT_TARGET=14.0 \
      CC=clang CXX=clang++ AR=ar RANLIB=ranlib NM=nm STRIP=strip
    ;;
  core|core-reset)
    [[ -x "$LUMA_TOOLS/autoreconf" && -f "$LUMA_CONTRIB/install/lib/pkgconfig/libavcodec.pc" ]] || { echo 'Build tools and contributed libraries first.' >&2; exit 2; }
    export PATH="$LUMA_TOOLS:$PATH"
    # A source archive nested in Luma must not acquire Luma's Git revision.
    export GIT_CEILING_DIRECTORIES="$(dirname "$LUMA_VLCROOT")"
    printf '%s\n' 'Luma-LGPL-79128878-VLCKit319ed2c0-p47' > "$LUMA_VLCROOT/src/revision.txt"
    # libtool's Darwin sysctl probe can be denied in a restricted build shell;
    # an empty result produces broken incremental linking. This conservative
    # bound is below getconf ARG_MAX and is sufficient for this build's links.
    [[ "$(getconf ARG_MAX)" -ge 65536 ]] || { echo 'Insufficient command argument limit.' >&2; exit 2; }
    export lt_cv_sys_max_cmd_len=65536
    cd "$LUMA_VLCROOT"
    ./bootstrap
    mkdir -p "$LUMA_CORE"
    cd "$LUMA_CORE"
    if [[ "${1:-}" == core-reset && -f Makefile ]]; then
      # Dependency deployment flags are not make inputs. A full core clean is
      # necessary after rebuilding static contributed libraries for another OS.
      make clean DEBUG=
    fi
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
    make -j"$LUMA_JOBS" DEBUG=
    make install DEBUG=
    echo 'Development install only. GPL-plugin/source/license/dependency audit must pass before distribution.'
    ;;
  *) echo "Usage: $0 tools|contrib|contrib-reset|core|core-reset" >&2; exit 2 ;;
esac
