#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 sdas
# See ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt.
"""Create a local custom AAR and independently signed debug APK replacement.

This proves the native replacement/relink mechanism without changing application
source or production signing. Outputs are development-only; no audit is approved.
"""
from __future__ import annotations
import argparse
import io
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import zipfile

ROOT=Path(__file__).resolve().parents[2]
NATIVE=ROOT/'ThirdParty/work/android-lgpl/libvlcjni/libvlc/jni/libs/arm64-v8a'
SOURCE_AAR=ROOT/'ThirdParty/work/android-lgpl/source-bindings-project/build/outputs/aar/luma-libvlc-source-bindings-release.aar'


def sha(path):
    with path.open('rb') as file:return hashlib.file_digest(file,'sha256').hexdigest()


def run(*args,env=None):
    return subprocess.check_output([str(a) for a in args],text=True,stderr=subprocess.STDOUT,env=env)


def rewrite(source,destination,native,prefix,metadata):
    with zipfile.ZipFile(source) as input_,zipfile.ZipFile(destination,'w') as output:
        if len(input_.infolist())>30000:raise ValueError('Too many APK/AAR entries')
        for entry in input_.infolist():
            if entry.filename=='assets/third-party/LUMA-NATIVE-DEVELOPMENT.json' or entry.filename.startswith(prefix+'/') or entry.filename.startswith('assets/lua/') or entry.filename.lower().endswith(('.lua','.luac')) or re.fullmatch(r'META-INF/(?:MANIFEST\.MF|[^/]+\.(?:SF|RSA|DSA|EC))',entry.filename,re.I):continue
            if entry.file_size>256*1024*1024:raise ValueError('Oversized APK/AAR entry')
            info=zipfile.ZipInfo(entry.filename,entry.date_time);info.compress_type=entry.compress_type;info.external_attr=entry.external_attr
            data=input_.read(entry.filename)
            if entry.filename=='classes.jar':
                source_jar=io.BytesIO(data);clean_jar=io.BytesIO()
                with zipfile.ZipFile(source_jar) as original,zipfile.ZipFile(clean_jar,'w') as cleaned:
                    for clazz in original.infolist():
                        if clazz.filename.startswith('org/videolan/libvlc/RendererDiscoverer'):continue
                        cleaned.writestr(clazz,original.read(clazz.filename))
                data=clean_jar.getvalue()
            output.writestr(info,data)
        for path in native:
            info=zipfile.ZipInfo(prefix+'/arm64-v8a/'+path.name);info.compress_type=zipfile.ZIP_STORED;output.writestr(info,path.read_bytes())
        output.writestr('assets/third-party/LUMA-NATIVE-DEVELOPMENT.json',json.dumps(metadata,indent=2)+'\n')


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True);parser.add_argument('--aar-only',action='store_true');parser.add_argument('--base-apk',type=Path);parser.add_argument('--test-apk',type=Path);args=parser.parse_args()
    output=args.output.resolve()
    if not output.is_relative_to(ROOT/'Android/.tools'):raise ValueError('Review outputs must stay in ignored Android/.tools')
    if output.exists():raise ValueError('Choose a new review output directory; existing files are never overwritten')
    base=args.base_apk or ROOT/'Android/app/build-custom-native/outputs/apk/debug/app-debug.apk';tests=args.test_apk or ROOT/'Android/app/build-custom-native/outputs/apk/androidTest/debug/app-debug-androidTest.apk'
    source_inventory=ROOT/'ThirdParty/work/android-lgpl/source-aar-inventory.json'
    if not SOURCE_AAR.is_file() or sha(SOURCE_AAR)!=json.loads(source_inventory.read_text())['sha256']:raise ValueError('Build and verify the source binding AAR before native review preparation')
    tools=ROOT/'Android/.tools/sdk/build-tools/36.0.0';java=Path(os.environ.get('JAVA_HOME',str(ROOT/'Android/.tools/jdk/jdk-17.0.21+9/Contents/Home')))
    environment=os.environ.copy();environment['JAVA_HOME']=str(java)
    if not args.aar_only:
        badging=run(tools/'aapt2','dump','badging',base)
        if 'application-debuggable' not in badging or "package: name='dev.sdas.luma.preview'" not in badging:raise ValueError('Only the Luma development APK can be relinked by this helper')
        with zipfile.ZipFile(base) as package:
            for name in package.namelist():
                if re.fullmatch(r'classes[0-9]*\.dex',name) and b'Lorg/videolan/libvlc/RendererDiscoverer' in package.read(name):raise ValueError('Base APK still contains excluded GPL binding DEX; compile the app from the filtered custom AAR first')
    native=[NATIVE/name for name in ('libvlc.so','libvlcjni.so','libc++_shared.so')]
    for path in native:
        data=path.read_bytes()
        if len(data)<64 or len(data)>128*1024*1024 or data[:6]!=b'\x7fELF\x02\x01' or struct.unpack_from('<H',data,18)[0]!=183:raise ValueError('Expected bounded ARM64 ELF library: '+path.name)
    output.mkdir(parents=True)
    metadata={'schema':1,'developmentOnly':True,'redistributionApproved':False,'architecture':'arm64-v8a',
        'sourceBindingsAarSha256':sha(SOURCE_AAR),'bindingClassesExcluded':['org/videolan/libvlc/RendererDiscoverer*.class'],
        'nativeLibraries':{path.name:sha(path) for path in native},
        'sourceState':json.loads((ROOT/'ThirdParty/work/android-lgpl/source-state.json').read_text()),
        'scope':'Independently signed local debug replacement test; source/module/dependency/notices audit still gates distribution.'}
    shutil.copyfile(SOURCE_AAR,output/'luma-libvlc-custom-arm64-development.aar')
    if args.aar_only:
        (output/'development-manifest.json').write_text(json.dumps(metadata,indent=2)+'\n');print('Created development-only filtered custom ARM64 AAR in '+str(output.relative_to(ROOT)));return
    metadata['baseDebugApkSha256']=sha(base)
    metadata['nativePatches']=json.loads((ROOT/'ThirdParty/work/android-lgpl/luma-native-patches.json').read_text())
    rewrite(base,output/'app-unsigned.apk',native,'lib',metadata)
    rewrite(tests,output/'test-unsigned.apk',[],'lib',metadata)
    # This public-password test key is newly generated, belongs only to this
    # disposable review directory, and is never a production/publisher key.
    key=output/'local-replacement-test.jks'
    run(java/'bin/keytool','-genkeypair','-keystore',key,'-storepass','android','-keypass','android',
        '-alias','luma-local-test','-dname','CN=Luma Local Replacement Test','-keyalg','RSA','-keysize','2048','-validity','3650','-noprompt',env=environment)
    key.chmod(0o600)
    for kind in ('app','test'):
        aligned=output/(kind+'-aligned.apk');signed=output/(kind+'-custom-debug.apk')
        run(tools/'zipalign','-P','16','-f','4',output/(kind+'-unsigned.apk'),aligned,env=environment)
        run(tools/'apksigner','sign','--ks',key,'--ks-key-alias','luma-local-test','--ks-pass','pass:android','--key-pass','pass:android','--out',signed,aligned,env=environment)
        run(tools/'zipalign','-c','-P','16','4',signed,env=environment);run(tools/'apksigner','verify',signed,env=environment)
    metadata['reviewOutputs']={p.name:sha(p) for p in output.glob('*.apk') if p.name.endswith('-custom-debug.apk')}
    metadata['customAarSha256']=sha(output/'luma-libvlc-custom-arm64-development.aar')
    (output/'development-manifest.json').write_text(json.dumps(metadata,indent=2)+'\n')
    print('Created development-only ARM64 AAR and independently signed app/test APKs in '+str(output.relative_to(ROOT)))
    print('Replacing the installed review app requires uninstalling its existing differently signed test copy; app-private test preferences are reset. No production key is used.')


if __name__=='__main__':
    try:main()
    except (OSError,ValueError,subprocess.CalledProcessError,zipfile.BadZipFile) as error:raise SystemExit('Native review preparation failed: '+str(error))
