#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Original build support; see ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt.
# Builds a development component only. It never builds/publishes a Luma EXE or approves a runtime.
set -euo pipefail
LUMA_REPO="$(cd "$(dirname "$0")/.." && pwd)"
LUMA_WORK="${1:-$LUMA_REPO/ThirdParty/work/windows-ci-lgpl}"
LUMA_OUTPUT="${2:-$LUMA_WORK/curated-component}"
LUMA_JOBS="${LUMA_WINDOWS_JOBS:-4}"
LUMA_MODE="${3:-build}"
[[ "$LUMA_MODE" == build || "$LUMA_MODE" == --probe-only ]] || { echo 'Third argument must be --probe-only or omitted.' >&2; exit 2; }
LUMA_SOURCE_SHA=48e0bf127711d829bcd49d35195b5e1b590f93d1bdd4263266dc4fd367641106
LUMA_SOURCE_COMMIT=6de05adcbaf2e8b85fe86aad4169393098628119
LUMA_IMAGE='registry.videolan.org/vlc-debian-llvm-ucrt@sha256:a0f9255792ee12da494efebdd203e8d11d769b7d938d5191920f76754d29f6e1'
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || { echo 'Run this helper in the pinned Linux amd64 container.' >&2; exit 2; }
[[ "$LUMA_WORK" == /* && "$LUMA_OUTPUT" == /* && "$LUMA_WORK" != *[[:space:]]* && "$LUMA_OUTPUT" != *[[:space:]]* ]] || { echo 'Use absolute owned paths without spaces.' >&2; exit 2; }
[[ "$LUMA_JOBS" =~ ^([1-9]|[12][0-9]|3[0-2])$ ]] || { echo 'LUMA_WINDOWS_JOBS must be1–32.' >&2; exit 2; }
[[ ! -e "$LUMA_OUTPUT" ]] || { echo 'Output already exists; choose a new owned destination.' >&2; exit 2; }
for tool in python3 curl make pkg-config autoconf automake autoreconf libtoolize cmake meson ninja nasm flex bison; do
 command -v "$tool" >/dev/null || { echo "Missing build tool: $tool" >&2; exit 2; }
done
mkdir -p "$LUMA_WORK/downloads" "$LUMA_WORK/logs" "$LUMA_WORK/audit"
LUMA_ARCHIVE="$LUMA_WORK/downloads/vlc-6de05adcba-research.tar.gz"
LUMA_SDK_ARCHIVE="$LUMA_WORK/downloads/llvm-mingw-20260908-ucrt-ubuntu-22.04-x86_64.tar.xz"
if [[ ! -f "$LUMA_SDK_ARCHIVE" ]]; then
 curl --fail --location --retry 3 --connect-timeout 20 --max-time 300 \
  'https://github.com/mstorsjo/llvm-mingw/releases/download/20260908/llvm-mingw-20260908-ucrt-ubuntu-22.04-x86_64.tar.xz' --output "$LUMA_SDK_ARCHIVE.partial"
 mv "$LUMA_SDK_ARCHIVE.partial" "$LUMA_SDK_ARCHIVE"
fi
python3 - "$LUMA_SDK_ARCHIVE" "$LUMA_WORK" <<'PYSDK'
from pathlib import Path
import hashlib,tarfile,sys
archive=Path(sys.argv[1]);work=Path(sys.argv[2]);expected='2258c745e3155870c80793f3e8c80b28fbde11b9ff73c4c78783635b3440b092'
with archive.open('rb') as stream:
 if hashlib.file_digest(stream,'sha256').hexdigest()!=expected:raise SystemExit('Pinned LLVM-MinGW SHA256 mismatch')
target=work/'llvm-mingw-20260908-ucrt-ubuntu-22.04-x86_64'
if not target.exists():
 with tarfile.open(archive) as tar:tar.extractall(work,filter='data')
if not (target/'LICENSE.TXT').is_file():raise SystemExit('Pinned compiler license missing')
PYSDK
LUMA_SDK="$LUMA_WORK/llvm-mingw-20260908-ucrt-ubuntu-22.04-x86_64"
export PATH="$LUMA_SDK/bin:$PATH"
LUMA_TOOLCHAIN_LICENSE="$LUMA_SDK/LICENSE.TXT"
# Fail in seconds on compiler/runtime or libtool C++ DLL incompatibility, before
# fetching/building any media dependency. A dedicated CI job can stop here.
bash "$LUMA_REPO/Scripts/prepare-libvlc-windows-toolchain-probe.sh" "$LUMA_SDK" "$LUMA_WORK/toolchain-probe" 2>&1 | tee "$LUMA_WORK/logs/toolchain-probe.log"
if [[ "$LUMA_MODE" == --probe-only ]]; then
 printf 'Toolchain-only proof: %s\n' "$LUMA_WORK/toolchain-probe/result.json"
 exit 0
fi
python3 - "$LUMA_WORK" "$LUMA_IMAGE" "$LUMA_SOURCE_COMMIT" <<'PY'
import json,subprocess,sys
from pathlib import Path
work=Path(sys.argv[1]);tools={}
for name in ['x86_64-w64-mingw32-gcc','x86_64-w64-mingw32-g++','x86_64-w64-mingw32-ar','cmake','meson','nasm','autoconf','automake','libtoolize']:
 p=subprocess.run([name,'--version'],capture_output=True,text=True,check=True);tools[name]=p.stdout.splitlines()[0]
(work/'audit/toolchain.json').write_text(json.dumps({'image':sys.argv[2],'sourceCommit':sys.argv[3],'tools':tools,'compilerArchive':'llvm-mingw-20260908-ucrt-ubuntu-22.04-x86_64.tar.xz','compilerSHA256':'2258c745e3155870c80793f3e8c80b28fbde11b9ff73c4c78783635b3440b092','bitForBitReproducible':False},indent=2)+'\n')
PY
if [[ ! -f "$LUMA_ARCHIVE" ]]; then
 curl --fail --location --retry 3 --connect-timeout 20 --max-time 300 \
  'https://codeload.github.com/videolan/vlc/tar.gz/6de05adcba' --output "$LUMA_ARCHIVE.partial"
 mv "$LUMA_ARCHIVE.partial" "$LUMA_ARCHIVE"
fi
python3 - "$LUMA_ARCHIVE" "$LUMA_SOURCE_SHA" "$LUMA_WORK" <<'PY'
from pathlib import Path,PurePosixPath
import hashlib,sys,tarfile
archive=Path(sys.argv[1]);work=Path(sys.argv[3]);actual=hashlib.file_digest(archive.open('rb'),'sha256').hexdigest()
if actual!=sys.argv[2]:raise SystemExit('Pinned VideoLAN source SHA256 mismatch')
source=work/'vlc-6de05adcba'
if source.exists():
 if not (work/'source-input.sha256').is_file() or (work/'source-input.sha256').read_text().strip()!=actual:raise SystemExit('Existing worktree has no matching input stamp')
else:
 with tarfile.open(archive) as t:
  members=t.getmembers()
  if len(members)>100000 or sum(m.size for m in members)>2*1024**3:raise SystemExit('Source archive limit exceeded')
  for m in members:
   p=PurePosixPath(m.name)
   if p.is_absolute() or '..' in p.parts or not p.parts or p.parts[0]!='vlc-6de05adcba' or not (m.isfile() or m.isdir()):raise SystemExit('Unsafe source archive entry')
  t.extractall(work,members=members)
 (work/'source-input.sha256').write_text(actual+'\n')
PY
LUMA_VLC="$LUMA_WORK/vlc-6de05adcba"
LUMA_CONTRIB="$LUMA_VLC/contrib/luma-windows-x64"
LUMA_PREFIX="$LUMA_WORK/contrib-prefix"
LUMA_INSTALL="$LUMA_WORK/unfiltered-install"
LUMA_BUILD_TRIPLET="$(cc -dumpmachine)"
export GIT_CEILING_DIRECTORIES="$LUMA_WORK"
export USE_FFMPEG=1 CMAKE_POLICY_VERSION_MINIMUM=3.5
unset PKG_CONFIG_PATH PKG_CONFIG_LIBDIR CC CXX CPP CXXCPP AR AS LD NM RANLIB RC WINDRES DLLTOOL STRIP CCAS CFLAGS CXXFLAGS CPPFLAGS LDFLAGS LIBS COMPILER_PATH LIBRARY_PATH CPATH C_INCLUDE_PATH CPLUS_INCLUDE_PATH GCC_EXEC_PREFIX CLANG_CONFIG_FILE_SYSTEM_DIR CLANG_CONFIG_FILE_USER_DIR DEBUG || true
mkdir -p "$LUMA_CONTRIB"
cd "$LUMA_CONTRIB"
../bootstrap --build="$LUMA_BUILD_TRIPLET" --host=x86_64-w64-mingw32 --prefix="$LUMA_PREFIX" \
 --disable-gpl --disable-gnuv3 --enable-ad-clauses --disable-sout --disable-disc --disable-all \
 --enable-ffmpeg --enable-opus --enable-matroska --enable-ass --enable-dav1d --enable-flac \
 --enable-mpg123 --enable-jpeg --enable-png --enable-libarchive --enable-zlib 2>&1 | tee "$LUMA_WORK/logs/contrib-bootstrap.log"
cat > config.mak <<'CONFIG'
CFLAGS := -O2 -D__MSVCRT_VERSION__=0xE00 -D_UCRT -D_WIN32_WINNT=0x0A00 -DWINVER=0x0A00
CXXFLAGS := -O2 -D__MSVCRT_VERSION__=0xE00 -D_UCRT -D_WIN32_WINNT=0x0A00 -DWINVER=0x0A00
LDFLAGS := -static-libstdc++
CONFIG
make list | tee "$LUMA_WORK/audit/contrib-selection.txt"
make -j"$LUMA_JOBS" fetch GNU=https://ftpmirror.gnu.org/gnu \
 VIDEOLAN=https://downloads.videolan.org/pub/videolan CONTRIB_VIDEOLAN=https://downloads.videolan.org/pub/contrib 2>&1 | tee "$LUMA_WORK/logs/contrib-fetch.log"
make -j"$LUMA_JOBS" GNU=https://ftpmirror.gnu.org/gnu \
 VIDEOLAN=https://downloads.videolan.org/pub/videolan CONTRIB_VIDEOLAN=https://downloads.videolan.org/pub/contrib 2>&1 | tee "$LUMA_WORK/logs/contrib-build.log"
cd "$LUMA_VLC"
printf '%s\n' 'Luma-LGPL-Windows-6de05adcba-r1' > src/revision.txt
./bootstrap 2>&1 | tee "$LUMA_WORK/logs/core-bootstrap.log"
cp m4/libtool.m4 "$LUMA_WORK/audit/libtool-before.m4"
python3 "$LUMA_REPO/Scripts/prepare-libvlc-windows-libtool.py" m4/libtool.m4 --receipt "$LUMA_WORK/audit/libtool-recognition-fix.json"
python3 - "$LUMA_WORK" "$LUMA_VLC" <<'PYLIBTOOL'
import difflib,json,sys
from pathlib import Path
work=Path(sys.argv[1]);source=Path(sys.argv[2]);before=(work/'audit/libtool-before.m4').read_text();after=(source/'m4/libtool.m4').read_text()
(work/'audit/libtool-recognition.patch').write_text(''.join(difflib.unified_diff(before.splitlines(keepends=True),after.splitlines(keepends=True),fromfile='m4/libtool.m4.before',tofile='m4/libtool.m4',n=4)))
(work/'audit/libtool-recognition-context.json').write_text(json.dumps({
 'purpose':'Preserve the official Clang compiler-rt archive in the hidden C++ dependencies used with -nostdlib; no custom stack-probe code',
 'gnuIssue':'https://debbugs.gnu.org/cgi/bugreport.cgi?bug=27866',
 'videoLANEquivalent':'https://code.videolan.org/videolan/vlc/-/blob/79128878/extras/tools/libtool-2.4.7-clang-libs.patch',
 'sourceOfBeforeMacro':'The pinned container GNU libtool2.5.4, copied into the owned VLC worktree by autoreconf --install',
 'regeneration':'autoconf --force after the one-line m4 change; system tools are not modified',
},indent=2)+'\n')
PYLIBTOOL
autoconf --force 2>&1 | tee "$LUMA_WORK/logs/core-autoconf.log"
mkdir -p build-luma-windows && cd build-luma-windows
export PKG_CONFIG_LIBDIR="$LUMA_PREFIX/lib/pkgconfig"
../configure --build="$LUMA_BUILD_TRIPLET" --host=x86_64-w64-mingw32 --prefix="$LUMA_INSTALL" \
 --with-contrib="$LUMA_PREFIX" --enable-shared --disable-static --disable-vlc --disable-qt --disable-skins2 \
 --disable-lua --disable-nls --disable-dbus --disable-vlm --disable-sout --disable-update-check --disable-notify \
 --disable-dvdread --disable-dvdnav --disable-bluray --disable-vcd --disable-addonmanagermodules \
 --disable-mad --disable-a52 --enable-avcodec --enable-merge-ffmpeg --enable-libass \
 CC=x86_64-w64-mingw32-gcc CXX=x86_64-w64-mingw32-g++ \
 CFLAGS='-O2 -D_WIN32_WINNT=0x0A00 -DWINVER=0x0A00' CXXFLAGS='-O2 -D_WIN32_WINNT=0x0A00 -DWINVER=0x0A00' \
 LDFLAGS='-static-libstdc++' LIBS='-lsynchronization' 2>&1 | tee "$LUMA_WORK/logs/core-configure.log"
make -j"$LUMA_JOBS" 2>&1 | tee "$LUMA_WORK/logs/core-build.log"
make install 2>&1 | tee "$LUMA_WORK/logs/core-install.log"
python3 "$LUMA_REPO/Scripts/prepare-libvlc-windows-inventory.py" --work "$LUMA_WORK" --output "$LUMA_WORK/source-evidence" --toolchain-license "$LUMA_TOOLCHAIN_LICENSE"
python3 "$LUMA_REPO/Scripts/prepare-libvlc-windows-audit.py" --work "$LUMA_WORK" --stage "$LUMA_OUTPUT"
printf 'Development component and complete source companion: %s\n' "$LUMA_OUTPUT"
printf '%s\n' 'Technical profile checks passed. Native Windows playback/replacement and independent approval remain separate; no Luma executable is packaged.'
