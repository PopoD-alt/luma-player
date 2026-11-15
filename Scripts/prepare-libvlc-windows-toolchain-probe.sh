#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Original build support; see ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt.
# No native code is executed: this checks PE linking on the build host.
set -euo pipefail
LUMA_REPO="$(cd "$(dirname "$0")/.." && pwd)"
LUMA_SDK="${1:?Usage: prepare-libvlc-windows-toolchain-probe.sh ABSOLUTE_SDK NEW_OUTPUT}"
LUMA_PROBE="${2:?Usage: prepare-libvlc-windows-toolchain-probe.sh ABSOLUTE_SDK NEW_OUTPUT}"
[[ "$LUMA_SDK" == /* && "$LUMA_PROBE" == /* && "$LUMA_SDK" != *[[:space:]]* && "$LUMA_PROBE" != *[[:space:]]* && ! -e "$LUMA_PROBE" ]] || { echo 'Use an existing absolute SDK and new owned space-free output directory.' >&2; exit 2; }
for tool in python3 make autoreconf autoconf; do command -v "$tool" >/dev/null; done
for tool in gcc g++ ar ranlib nm windres dlltool objdump strip; do [[ -x "$LUMA_SDK/bin/x86_64-w64-mingw32-$tool" ]] || { echo "Missing pinned SDK tool: $tool" >&2; exit 2; }; done
mkdir -p "$LUMA_PROBE"
python3 - "$LUMA_PROBE" <<'PY'
import json,os,sys
from pathlib import Path
# Deliberate allowlist: never copy the complete CI environment or credentials.
names='CC CXX CPP CXXCPP AR AS LD NM RANLIB RC WINDRES DLLTOOL STRIP CCAS CFLAGS CXXFLAGS CPPFLAGS LDFLAGS LIBS COMPILER_PATH LIBRARY_PATH CPATH C_INCLUDE_PATH CPLUS_INCLUDE_PATH GCC_EXEC_PREFIX CLANG_CONFIG_FILE_SYSTEM_DIR CLANG_CONFIG_FILE_USER_DIR'.split()
Path(sys.argv[1],'inherited-tool-environment.json').write_text(json.dumps({n:os.environ[n] for n in names if n in os.environ},indent=2)+'\n')
PY
unset CC CXX CPP CXXCPP AR AS LD NM RANLIB RC WINDRES DLLTOOL STRIP CCAS CFLAGS CXXFLAGS CPPFLAGS LDFLAGS LIBS COMPILER_PATH LIBRARY_PATH CPATH C_INCLUDE_PATH CPLUS_INCLUDE_PATH GCC_EXEC_PREFIX CLANG_CONFIG_FILE_SYSTEM_DIR CLANG_CONFIG_FILE_USER_DIR || true
export PATH="$LUMA_SDK/bin:$PATH"
LUMA_CC="$LUMA_SDK/bin/x86_64-w64-mingw32-gcc"
LUMA_CXX="$LUMA_SDK/bin/x86_64-w64-mingw32-g++"
LUMA_NM="$LUMA_SDK/bin/x86_64-w64-mingw32-nm"
LUMA_BUILTINS="$("$LUMA_CC" -print-libgcc-file-name)"
[[ -f "$LUMA_BUILTINS" && "$LUMA_BUILTINS" == "$LUMA_SDK"/*/libclang_rt.builtins-x86_64.a ]] || { echo "Unexpected compiler runtime path: $LUMA_BUILTINS" >&2; exit 1; }
"$LUMA_CC" --version
"$LUMA_CXX" --version
"$LUMA_NM" --defined-only "$LUMA_BUILTINS" > "$LUMA_PROBE/builtins-symbols.txt"
python3 - "$LUMA_PROBE/builtins-symbols.txt" <<'PY'
import re,sys
if not re.search(r'\bT\s+___chkstk_ms\s*$',open(sys.argv[1]).read(),re.M):raise SystemExit('Pinned compiler runtime does not define the Windows stack probe')
PY
cat > "$LUMA_PROBE/stack.c" <<'C'
#include <stddef.h>
__declspec(dllexport) __declspec(noinline) unsigned luma_stack_c(unsigned seed) {
    volatile unsigned char bytes[65536];
    for (size_t i = 0; i < sizeof(bytes); ++i) bytes[i] = (unsigned char)(seed + i);
    return bytes[seed & 65535] + bytes[(seed + 32768) & 65535];
}
C
cat > "$LUMA_PROBE/stack.cpp" <<'CXX'
#include <string>
#include <stdexcept>
#include <cstddef>
extern "C" __declspec(dllexport) __declspec(noinline) unsigned luma_stack_cxx(unsigned seed) {
    volatile unsigned char bytes[65536];
    for (std::size_t i = 0; i < sizeof(bytes); ++i) bytes[i] = static_cast<unsigned char>(seed + i);
    std::string text = std::to_string(seed);
    if (seed == 0xffffffffu) throw std::runtime_error(text);
    return bytes[seed & 65535] + bytes[(seed + 32768) & 65535] + static_cast<unsigned>(text.size());
}
CXX
cd "$LUMA_PROBE"
"$LUMA_CC" -O2 -c stack.c -o stack-c.o
"$LUMA_CXX" -O2 -c stack.cpp -o stack-cxx.o
"$LUMA_NM" --undefined-only stack-c.o stack-cxx.o > stack-undefined.txt
python3 - "$LUMA_PROBE/stack-undefined.txt" <<'PY'
import sys
if open(sys.argv[1]).read().count('___chkstk_ms')<2:raise SystemExit('Probe optimization unexpectedly removed a large stack allocation')
PY
"$LUMA_CC" -shared -O2 -static-libstdc++ -v stack-c.o -o direct-c.dll
"$LUMA_CXX" -shared -O2 -static-libstdc++ -v stack-cxx.o -o direct-cxx.dll
printf '%s\n' 'PASS direct C and C++ DLLs with actual large-stack and C++ runtime references'
mkdir -p autotools/m4 && cd autotools
cp ../stack.c c_stack.c
cp ../stack.cpp cxx_stack.cpp
cat > configure.ac <<'CONF'
AC_INIT([luma-original-windows-link-probe],[1])
AC_CONFIG_SRCDIR([c_stack.c])
AC_CONFIG_MACRO_DIRS([m4])
AM_INIT_AUTOMAKE([foreign])
AC_PROG_CC
AC_PROG_CXX
AM_PROG_AR
# Match VLC's own configure.ac: its static contrib/compiler archives must be
# allowed in dynamic plugins instead of the generic DLL-only dependency test.
AC_CACHE_VAL([lt_cv_deplibs_check_method],[lt_cv_deplibs_check_method=pass_all])
LT_INIT([win32-dll])
AC_CONFIG_FILES([Makefile])
AC_OUTPUT
CONF
cat > Makefile.am <<'MAKE'
ACLOCAL_AMFLAGS = -I m4
lib_LTLIBRARIES = libstack_c.la libstack_cxx.la
libstack_c_la_SOURCES = c_stack.c
libstack_c_la_LDFLAGS = -module -avoid-version -no-undefined
libstack_cxx_la_SOURCES = cxx_stack.cpp
libstack_cxx_la_LDFLAGS = -module -avoid-version -no-undefined
MAKE
autoreconf --install --force --verbose
LUMA_BUILD_TRIPLET="$(cc -dumpmachine)"
configure_probe() {
 ./configure --build="$LUMA_BUILD_TRIPLET" --host=x86_64-w64-mingw32 --enable-shared --disable-static \
  CC="$LUMA_CC" CXX="$LUMA_CXX" AR="$LUMA_SDK/bin/x86_64-w64-mingw32-ar" \
  RANLIB="$LUMA_SDK/bin/x86_64-w64-mingw32-ranlib" NM="$LUMA_NM" \
  CFLAGS='-O2 -D_WIN32_WINNT=0x0A00 -DWINVER=0x0A00' CXXFLAGS='-O2 -D_WIN32_WINNT=0x0A00 -DWINVER=0x0A00' \
  LDFLAGS='-static-libstdc++' LIBS='-lsynchronization'
}
configure_probe > ../configure-before.log 2>&1
cp libtool ../libtool-before
set +e
make V=1 > ../libtool-before.log 2>&1
LUMA_BEFORE=$?
set -e
printf 'Unmodified libtool probe exit: %s\n' "$LUMA_BEFORE"
tail -n 18 ../libtool-before.log
make clean >/dev/null
python3 "$LUMA_REPO/Scripts/prepare-libvlc-windows-libtool.py" m4/libtool.m4 --receipt ../libtool-recognition-fix.json
autoconf --force
configure_probe > ../configure-after.log 2>&1
cp libtool ../libtool-after
make V=1 > ../libtool-after.log 2>&1 || { cat ../libtool-after.log; exit 1; }
python3 - "$LUMA_PROBE" "$LUMA_BUILTINS" "$LUMA_BEFORE" <<'PY'
import hashlib,json,sys
from pathlib import Path
p=Path(sys.argv[1]);runtime=sys.argv[2]
libtool=(p/'libtool-after').read_text()
if runtime not in libtool:raise SystemExit('Libtool still omitted the official compiler runtime')
files=['direct-c.dll','direct-cxx.dll','autotools/.libs/libstack_c.dll','autotools/.libs/libstack_cxx.dll']
for f in files:
 data=(p/f).read_bytes()
 if data[:2]!=b'MZ':raise SystemExit('Probe is not a PE DLL: '+f)
report={'passed':True,'nativeExecution':False,'unmodifiedLibtoolExit':int(sys.argv[3]),'compilerRuntime':runtime,'files':{f:hashlib.sha256((p/f).read_bytes()).hexdigest() for f in files}}
(p/'result.json').write_text(json.dumps(report,indent=2)+'\n')
PY
printf '%s\n' 'PASS libtool C and C++ DLLs retain the official compiler runtime; no native playback claim'
