#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 sdas
# See ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt.
"""Build an Android arm64 development libVLC from pinned native sources.

No release approval is created. LGPL contrib selection is combined with a
conservative per-module source/header filter before static JNI linkage.
"""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

REPO = Path(__file__).resolve().parents[2]
WORK = REPO / 'ThirdParty/work/android-lgpl'
JNI = WORK / 'libvlcjni'
VLC = WORK / 'vlc'
INPUTS = (
    ('libvlcjni-0b8dc65e.tar.gz', 'bafba3f63830d7a049d753f8ffe6fecdcf0abcfed8e449355e87205da18854ec',
     'libvlcjni-0b8dc65efb203c86a0476bc337ddd99ecf1c0ef6/', JNI,
     'https://code.videolan.org/videolan/libvlcjni/-/archive/0b8dc65efb203c86a0476bc337ddd99ecf1c0ef6/libvlcjni-0b8dc65efb203c86a0476bc337ddd99ecf1c0ef6.tar.gz'),
    ('vlc-84177b22.tar.gz', 'aeb337f3487011d5f826b360b47c32c28fa3dbca737ac638b91f8ce31147ad20',
     'vlc-84177b2273abc5c4300234e6e18b55a4d2dd5a03/', VLC,
     'https://codeload.github.com/videolan/vlc/tar.gz/84177b2273abc5c4300234e6e18b55a4d2dd5a03'),
)

def sha(path):
    with path.open('rb') as source:return hashlib.file_digest(source, 'sha256').hexdigest()

def prepare():
    WORK.mkdir(parents=True, exist_ok=True)
    spec = importlib.util.spec_from_file_location('luma_pinned_archive', REPO/'Scripts/prepare-libvlc.py')
    helper=importlib.util.module_from_spec(spec);spec.loader.exec_module(helper)
    records=[]
    for filename, expected, prefix, output, url in INPUTS:
        archive=REPO/'ThirdParty/downloads'/filename
        if not archive.exists():
            helper.obtain({'filename':filename,'sha256':expected,'maximumBytes':100*1024*1024,'url':url},archive.parent)
        if sha(archive)!=expected:raise ValueError('Pinned source mismatch: '+filename)
        if not output.exists():
            output.mkdir();helper.extract_archive(archive,output,{'kind':'tar','archivePrefix':prefix})
        records.append({'archive':filename,'sha256':expected,'url':url})
    patches=sorted((JNI/'libvlc/patches').glob('*.patch'))
    if len(patches)!=20:raise ValueError('Expected exact JNI20-patch source series')
    patch_hashes={p.name:sha(p) for p in patches}
    stamp=WORK/'source-state.json'
    state={'inputs':records,'upstreamPatches':patch_hashes}
    if stamp.exists():
        if json.loads(stamp.read_text())!=state:raise ValueError('Existing source state differs; use a fresh isolated work directory')
    else:
        for patch in patches:
            subprocess.run(['git','apply','--directory=ThirdParty/work/android-lgpl/vlc',str(patch)],cwd=REPO,check=True)
        stamp.write_text(json.dumps(state,indent=2)+'\n')
    original=JNI/'buildsystem/compile-libvlc.sh'
    custom=JNI/'buildsystem/compile-luma-lgpl.sh'
    text=original.read_text()
    text=text.replace('--build=x86_64-unknown-linux', '--build="$(sh "$VLC_SRC_DIR/autotools/config.guess")"')
    # Source/subtitle playback needs no Lua scripts or embedded stream-output services.
    text=text.replace('--enable-lua', '--disable-lua').replace('--enable-sout', '--disable-sout')
    text=text.replace('AVLC_CONTRIB_LICENSE=g','AVLC_CONTRIB_LICENSE=a')
    # Both calls to module discovery are filtered; no excluded archive enters the
    # redefined plugin set or final libvlc.so. The first call writes the audit.
    old='echo "$(find $1 -name \'lib*plugin.a\' | grep -vE "lib(${blacklist_regexp})_plugin.a" | tr \'\\n\' \' \')"'
    new='python3 "'+str(REPO/'Android/scripts/filter-native-modules.py')+'" --source "$VLC_SRC_DIR" --build "$VLC_BUILD_DIR" --directory "$1" --blacklist "$blacklist_regexp" --audit "'+str(WORK/'android-module-audit.json')+'"'
    if text.count(old)!=1:raise ValueError('Upstream module-discovery recipe changed')
    text=text.replace(old,new)
    custom.write_text(text);custom.chmod(0o755)
    (WORK/'build-profile.json').write_text(json.dumps({'architecture':'arm64-v8a','ndk':'29.0.14206865','api':21,'contribLicense':'LGPL2.1/noGNUv3/ad-clauses','sourceState':state,'recipeSha256':sha(custom),'redistributionApproved':False},indent=2)+'\n')
    patch=REPO/'Android/scripts/patches/ffmpeg-lgpl-aarch64-portable.patch'
    target=VLC/'contrib/src/ffmpeg/luma-lgpl-aarch64-portable.patch'
    shutil.copyfile(patch,target)
    rules=VLC/'contrib/src/ffmpeg/rules.mak';recipe=rules.read_text()
    hook='\t$(APPLY) $(SRC)/ffmpeg/luma-lgpl-aarch64-portable.patch\n'
    if hook not in recipe:
        anchor='\t$(APPLY) $(SRC)/ffmpeg/avcodec-fix-compilation-visionos.patch\n'
        if recipe.count(anchor)!=1:raise ValueError('FFmpeg recipe patch anchor changed')
        rules.write_text(recipe.replace(anchor,anchor+hook))
    unpacked=VLC/'contrib/contrib-android-aarch64-linux-android/ffmpeg'
    if unpacked.exists():
        marker=unpacked/'.luma-lgpl-portable-patch'
        if marker.exists():
            if marker.read_text().strip()!=sha(patch):raise ValueError('Existing FFmpeg LGPL patch differs')
        else:
            subprocess.run(['git','apply','--directory=ThirdParty/work/android-lgpl/vlc/contrib/contrib-android-aarch64-linux-android/ffmpeg',str(patch)],cwd=REPO,check=True)
            marker.write_text(sha(patch)+'\n')
    recipe=rules.read_text();marker_hook='\techo '+sha(patch)+' > $(UNPACK_DIR)/.luma-lgpl-portable-patch\n'
    if marker_hook not in recipe:rules.write_text(recipe.replace(hook,hook+marker_hook))
    jni_patch=REPO/'Android/scripts/patches/jni-no-renderer-discovery.patch';jni_stamp=WORK/'jni-no-renderer-patch.json'
    jni_state={'sha256':sha(jni_patch)}
    if jni_stamp.exists():
        if json.loads(jni_stamp.read_text())!=jni_state:raise ValueError('Existing JNI exclusion patch differs')
    else:
        subprocess.run(['git','apply','--directory=ThirdParty/work/android-lgpl/libvlcjni',str(jni_patch)],cwd=REPO,check=True)
        jni_stamp.write_text(json.dumps(jni_state,indent=2)+'\n')
    (WORK/'luma-native-patches.json').write_text(json.dumps({'ffmpegPortablePatchSha256':sha(patch),'jniNoRendererPatchSha256':sha(jni_patch),'scope':'Three GPL-granted FFmpeg optimized units and unused renderer-discovery JNI initialization excluded; existing LGPL portable C retained'},indent=2)+'\n')
    (VLC/'src/revision.txt').write_text('Luma-LGPL-84177b22-JNI0b8dc65e-p20-ffmpeg-portable-r2-no-renderer\n')
    return custom

def build():
    script=prepare()
    ndk=REPO/'Android/.tools/sdk/ndk/29.0.14206865'
    if not (ndk/'source.properties').is_file():raise ValueError('Install project-local Android NDK29.0.14206865 first')
    environment=os.environ.copy()
    environment.update(ANDROID_NDK=str(ndk),ANDROID_HOME=str(REPO/'Android/.tools/sdk'),
                       ANDROID_USER_HOME=str(REPO/'Android/.tools/android-user'),
                       JAVA_HOME=str(REPO/'Android/.tools/jdk/jdk-17.0.21+9/Contents/Home'),
                       MAKEFLAGS='-j'+os.environ.get('LUMA_LIBVLC_JOBS','8'),DEBUG='',
                       GIT_CEILING_DIRECTORIES=str(WORK),lt_cv_sys_max_cmd_len='65536')
    # Reuse independently built host build tools when present, without changing
    # or linking their source/output directories. VLC bootstrap still validates versions.
    tools=REPO/'ThirdParty/work/vlc-79128878/extras/tools/build/bin'
    environment['PATH']=str(tools)+os.pathsep+environment.get('PATH','')
    tarballs=VLC/'contrib/tarballs';tarballs.mkdir(parents=True,exist_ok=True)
    cache=REPO/'ThirdParty/work/vlc-79128878/contrib/tarballs'
    for archive in cache.glob('*'):
        target=tarballs/archive.name
        if archive.is_file() and not target.exists():shutil.copy2(archive,target)
    subprocess.run([str(script),'-a','arm64','--release','--license','a'],cwd=VLC,env=environment,check=True)
    print('Development native build completed; module/dependency/source/replacement audit still gates release.')

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('command',choices=['prepare','build']);args=parser.parse_args()
    try:prepare() if args.command=='prepare' else build()
    except (OSError,ValueError,subprocess.CalledProcessError) as error:print('ERROR:',error,file=sys.stderr);raise SystemExit(1)
