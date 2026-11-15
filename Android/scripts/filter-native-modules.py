#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 sdas
# See ThirdParty/libvlc/LUMA-BUILD-SUPPORT-LICENSE.txt.
"""Fail-closed static module selection from actual generated Android SOURCES.

Source/header evidence supplements VLC's default LGPL metadata. This does not
approve the core, contributed dependencies, redistribution or replacement terms.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

GPL=re.compile(r'GNU\s+General\s+Public\s+License|VLC_MODULE_LICENSE\s+VLC_LICENSE_GPL')
QUOTED=re.compile(r'^\s*#\s*include\s*"([^\"]+)"',re.M)

def sha(path):
    with path.open('rb') as file:return hashlib.file_digest(file,'sha256').hexdigest()

def mapping(build):
    helper=build/'luma-module-sources.mk'
    helper.write_text('.PHONY: luma-module-sources\nluma-module-sources:\n\t@$(foreach v,$(filter %_la_SOURCES %_la_LIBADD,$(.VARIABLES)),$(info $(v)=$($(v))))\n')
    result=subprocess.check_output(['make','-f','Makefile','-f',str(helper),'luma-module-sources'],cwd=build/'modules',text=True)
    variables={}
    for line in result.splitlines():
        if line.startswith('lib') and '=' in line:
            name,files=line.split('=',1);variables[name]=files.split()
    def expand(name, visited):
        if name in visited:return set()
        visited.add(name);files=set(variables.get(name+'_la_SOURCES',[]))
        for dependency in variables.get(name+'_la_LIBADD',[]):
            if dependency.endswith('.la'):
                files.update(expand(Path(dependency).stem.replace('-','_'),visited))
        return files
    values={name.removesuffix('_la_SOURCES'):sorted(expand(name.removesuffix('_la_SOURCES'),set()))
            for name in variables if name.endswith('_plugin_la_SOURCES')}
    if not values:raise ValueError('Actual Android module source mapping unavailable')
    return values

def inspect(root,names):
    root=root.resolve();pending=[root/'modules'/name for name in names];seen={}
    while pending:
        path=pending.pop().resolve()
        if path in seen or not path.is_relative_to(root) or not path.is_file():continue
        text=path.read_text(errors='replace')
        overrides=re.findall(r'^\s*#\s*define\s+VLC_MODULE_LICENSE\s+([^\r\n]+)',text,re.M)
        unknown=any(value.strip()!='VLC_LICENSE_LGPL_2_1_PLUS' for value in overrides)
        seen[path]={'path':path.relative_to(root).as_posix(),'sha256':sha(path),'gplNotice':bool(GPL.search(text)),'unreviewedLicenseOverride':unknown}
        pending.extend(path.parent/include for include in QUOTED.findall(text))
    return sorted(seen.values(),key=lambda item:item['path'])

def filter_(args):
    source=args.source.resolve();build=args.build.resolve();directory=args.directory.resolve()
    header=(source/'include/vlc_plugin.h').read_text()
    if '# define VLC_MODULE_LICENSE VLC_LICENSE_LGPL_2_1_PLUS' not in header and '#define VLC_MODULE_LICENSE VLC_LICENSE_LGPL_2_1_PLUS' not in header:
        if not re.search(r'#\s*define\s+VLC_MODULE_LICENSE\s+VLC_LICENSE_LGPL_2_1_PLUS',header):raise ValueError('Upstream default module metadata changed')
    sources=mapping(build);records=[];selected=[];blocked=re.compile(r'lib('+args.blacklist+r')_plugin\.a') if args.blacklist else None
    for archive in sorted(directory.rglob('lib*plugin.a')):
        reasons=[];names=sources.get(archive.stem,[]);files=inspect(source,names)
        if blocked and blocked.search(archive.name):reasons.append('Upstream Android blacklist')
        if not names or not files:reasons.append('Expanded source mapping unavailable')
        if any(item['gplNotice'] for item in files):reasons.append('Conservatively excluded GPL-marked source or quoted header')
        if any(item['unreviewedLicenseOverride'] for item in files):reasons.append('Unreviewed explicit module-license override')
        record={'module':archive.stem.removeprefix('lib').removesuffix('_plugin'),'archive':str(archive.relative_to(directory)),
                'sha256':sha(archive),'retained':not reasons,'reasons':reasons,'sources':files}
        records.append(record)
        if not reasons:selected.append(str(archive))
    if not records or not selected:raise ValueError('No inspected LGPL-compatible static modules selected')
    audit_path=args.audit.with_name(args.audit.stem+'-final-link'+args.audit.suffix) if directory.is_relative_to(build/'install') else args.audit
    audit_path.write_text(json.dumps({'schema':1,'platform':'android-arm64-v8a','sourceBase':'84177b2273abc5c4300234e6e18b55a4d2dd5a03','patchSet':'libvlcjni0b8dc65e:20',
       'selection':'Actual expanded Android module source/quoted-header inventory; upstream blacklist and GPL/uncertain sources excluded before static linkage',
       'limitations':'Source-based module filter only; core/contrib license/source/runtime metadata and replacement mechanism require separate validation. No redistribution approval.',
       'retained':sum(item['retained'] for item in records),'excluded':sum(not item['retained'] for item in records),'modules':records},indent=2)+'\n')
    print(' '.join(selected))

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True);parser.add_argument('--build',type=Path,required=True);parser.add_argument('--directory',type=Path,required=True);parser.add_argument('--blacklist',required=True);parser.add_argument('--audit',type=Path,required=True)
    try:filter_(parser.parse_args())
    except (OSError,ValueError,subprocess.CalledProcessError) as error:print('Module filtering failed: '+str(error),file=sys.stderr);raise SystemExit(1)
