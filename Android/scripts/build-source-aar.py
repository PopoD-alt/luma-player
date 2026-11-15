#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 sdas
# See ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt.
"""Build the exact LGPL Java/JNI binding subset directly from source.

Uses frozen native libraries from build-lgpl-runtime.py; no stock AAR bytecode,
Lua/HRTF assets, publication plugin, or parent-repository Git metadata is used.
"""
from pathlib import Path
import hashlib
import json
import os
import re
import shutil
import subprocess
import zipfile
import io

ROOT=Path(__file__).resolve().parents[2]
WORK=ROOT/'ThirdParty/work/android-lgpl'
JNI=WORK/'libvlcjni/libvlc'
PROJECT=WORK/'source-bindings-project'


def sha(path):
    with path.open('rb') as file:return hashlib.file_digest(file,'sha256').hexdigest()


def main():
    PROJECT.mkdir(exist_ok=True)
    (PROJECT/'gradle').mkdir(exist_ok=True)
    shutil.copyfile(ROOT/'Android/gradle/verification-metadata.xml',PROJECT/'gradle/verification-metadata.xml')
    (PROJECT/'settings.gradle').write_text("""pluginManagement {
    repositories { google(); mavenCentral(); gradlePluginPortal() }
    resolutionStrategy { eachPlugin { if(requested.id.id == 'com.android.library') useModule('com.android.tools.build:gradle:9.4.0') } }
}
dependencyResolutionManagement { repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS); repositories { google(); mavenCentral() } }
rootProject.name = 'luma-libvlc-source-bindings'
""")
    (PROJECT/'gradle.properties').write_text('android.useAndroidX=true\norg.gradle.jvmargs=-Xmx2048m -Dfile.encoding=UTF-8\n')
    manifest=(JNI/'AndroidManifest.xml').read_text();manifest=re.sub(r'\s+(?:package|android:versionCode|android:versionName)="[^"]*"','',manifest)
    (PROJECT/'AndroidManifest.xml').write_text(manifest)
    (PROJECT/'consumer-rules.pro').write_text('-keep class org.videolan.libvlc.** { *; }\n')
    sources=JNI/'src';resources=JNI/'res';native=JNI/'jni/libs';evidence=PROJECT/'assets/third-party';evidence.mkdir(parents=True,exist_ok=True)
    source_hashes={p.relative_to(sources).as_posix():sha(p) for p in sorted(sources.rglob('*.java')) if p.name!='RendererDiscoverer.java'}
    metadata={'schema':1,'bindingsSource':'libvlcjni0b8dc65efb203c86a0476bc337ddd99ecf1c0ef6','javaSources':source_hashes,
        'excludedJavaSource':'org/videolan/libvlc/RendererDiscoverer.java','excludedOptionalAssets':['lua','hrtf'],
        'nativeLibraries':{p.name:sha(p) for p in sorted((native/'arm64-v8a').glob('*.so'))},
        'nativeRevision':(WORK/'vlc/src/revision.txt').read_text().strip(),
        'patches':json.loads((WORK/'luma-native-patches.json').read_text()),'approvalAuthority':'ThirdParty/libvlc/packages.json'}
    (evidence/'LUMA-SOURCE-BUILD.json').write_text(json.dumps(metadata,indent=2)+'\n')
    # Paths come only from the known local source tree; JSON string syntax is also
    # valid for these literal Groovy file strings (no shell evaluation occurs).
    q=lambda p:json.dumps(str(p))
    (PROJECT/'build.gradle').write_text("""plugins { id 'com.android.library' version '9.4.0' }
android {
    namespace = 'org.videolan'
    compileSdk = 37
    buildToolsVersion = '36.0.0'
    defaultConfig {
        minSdk = 21
        consumerProguardFiles 'consumer-rules.pro'
        resValue 'string', 'build_libvlc_revision', '0b8dc65efb203c86a0476bc337ddd99ecf1c0ef6-Luma-no-renderer'
        resValue 'string', 'build_vlc_revision', '"""+metadata['nativeRevision']+"""'
    }
    buildFeatures { buildConfig = true; resValues = true }
    sourceSets {
        main {
            manifest.srcFile 'AndroidManifest.xml'
            java.srcDirs = ["""+q(sources)+"""]
            java.exclude '**/RendererDiscoverer.java'
            res.srcDirs = ["""+q(resources)+"""]
            jniLibs.srcDirs = ["""+q(native)+"""]
            assets.srcDirs = ['assets']
        }
    }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    packaging { jniLibs { keepDebugSymbols += ['**/libvlc.so','**/libvlcjni.so','**/libc++_shared.so'] } }
    buildTypes { release { minifyEnabled = false } }
}
dependencies {
    implementation 'androidx.annotation:annotation:1.7.1'
    implementation 'androidx.legacy:legacy-support-v4:1.0.0'
    implementation 'org.jetbrains.kotlin:kotlin-stdlib:2.2.10'
}
""")
    environment=os.environ.copy();environment.update(JAVA_HOME=str(ROOT/'Android/.tools/jdk/jdk-17.0.21+9/Contents/Home'),ANDROID_HOME=str(ROOT/'Android/.tools/sdk'),ANDROID_USER_HOME=str(ROOT/'Android/.tools/android-user'),GRADLE_USER_HOME=str(ROOT/'Android/.tools/gradle-fresh-verification'))
    subprocess.run([str(ROOT/'Android/gradlew'),'-p',str(PROJECT),'--no-daemon','--max-workers=2','assembleRelease'],env=environment,check=True)
    aar=PROJECT/'build/outputs/aar/luma-libvlc-source-bindings-release.aar'
    with zipfile.ZipFile(aar) as package:
        assert not any('/lua/' in name.lower() or name.lower().endswith(('.lua','.luac')) for name in package.namelist())
        libraries=[name for name in package.namelist() if name.endswith('.so')]
        assert len(libraries)==3 and all(name.startswith('jni/arm64-v8a/') for name in libraries)
        for name in libraries:assert hashlib.sha256(package.read(name)).hexdigest()==metadata['nativeLibraries'][Path(name).name]
        with zipfile.ZipFile(io.BytesIO(package.read('classes.jar'))) as classes:assert not any('RendererDiscoverer' in name for name in classes.namelist())
    (WORK/'source-aar-inventory.json').write_text(json.dumps({'path':str(aar.relative_to(ROOT)),'sha256':sha(aar),'bytes':aar.stat().st_size,'metadata':metadata},indent=2)+'\n')
    print('PASS: source-built binding AAR, excluded GPL Java class, exact3ARM64 native libraries, no Lua/HRTF assets: '+str(aar.relative_to(ROOT)))

if __name__=='__main__':main()
