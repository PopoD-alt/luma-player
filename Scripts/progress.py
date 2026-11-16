#!/usr/bin/env python3
"""Record actual local check results and render a self-contained progress canvas.

This does not start background codec work or publish anything. A canvas is a
snapshot, rewritten when this command runs. Native checks need media services.
"""
import argparse
import base64
import datetime as dt
import fcntl
import hashlib
import json
import os
from pathlib import Path
import plistlib
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
PROGRESS = ROOT / 'Progress'
CANVAS_TARGET = None
CHECKS = {
    'msvideo1': 'test-msvideo1.sh', 'pgs': 'test-pgs.sh',
    'ms-adpcm': 'test-ms-adpcm.sh', 'native-formats': 'test-native-formats.sh', 'pgs-playback': 'test-pgs-playback.sh',
    'codecs': 'test-codecs.sh', 'flac': 'test-flac.sh', 'jpeg': 'test-jpeg.sh',
    'matroska': 'test-matroska.sh', 'audio': 'test.sh', 'video': 'test-video.sh',
    'fullscreen': 'test-fullscreen.sh', 'opus': 'test-opus.sh',
    'hevc': 'test-hevc.sh', 'aac': 'test-aac.sh', 'updates': 'test-updates.sh',
    'release-plan': 'test-release-plan.sh', 'windows-core': 'test-windows-core.sh', 'windows-remux': 'test-windows-remux.sh'
}

def now():
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec='seconds')

def read(path, default):
    return json.loads(path.read_text()) if path.exists() else default

def atomic(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode='w', dir=path.parent, delete=False, encoding='utf-8') as stream:
        stream.write(content)
        temporary = Path(stream.name)
    temporary.replace(path)

def digest(scope='macos'):
    result = hashlib.sha256()
    files = sorted((ROOT / 'Sources').glob('*.swift'))
    if scope == 'windows':
        files = sorted(path for path in (ROOT / 'Windows').rglob('*')
                       if path.is_file() and path.suffix in {'.cs', '.csproj', '.props', '.json', '.manifest', '.ps1', '.swift'}
                       and not {'bin', 'obj', 'artifacts'}.intersection(path.relative_to(ROOT / 'Windows').parts))
    for path in files:
        name = str(path.relative_to(ROOT)) if scope == 'windows' else path.name
        result.update(name.encode()); result.update(path.read_bytes())
    return result.hexdigest()

def update_check(identifier, value):
    PROGRESS.mkdir(exist_ok=True)
    with (PROGRESS / '.checks.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        records = read(PROGRESS / 'checks.json', {})
        records[identifier] = value
        atomic(PROGRESS / 'checks.json', json.dumps(records, indent=2) + '\n')
        if value['status'] != 'running':
            with (PROGRESS / 'check-history.jsonl').open('a') as history:
                history.write(json.dumps(value) + '\n')

def snapshot():
    catalog = read(ROOT / 'CodecSupport.json', {'entries': []})
    windows_catalog = read(ROOT / 'Windows/CodecSupport.json', {'entries': []})
    plan = read(PROGRESS / 'work-items.json', {'items': []})
    records = read(PROGRESS / 'checks.json', {})
    history = []
    history_file = PROGRESS / 'check-history.jsonl'
    if history_file.exists():
        for line in history_file.read_text().splitlines():
            try: history.append(json.loads(line))
            except json.JSONDecodeError: pass
    source_digest = digest()
    for record in records.values():
        record['matches_current_source'] = record.get('source_digest') == digest(record.get('source_scope', 'macos'))
    built = ROOT / 'build/Luma Player.app/Contents/Info.plist'
    version = plistlib.loads(built.read_bytes()).get('CFBundleShortVersionString') if built.exists() else None
    evidence = read(PROGRESS / 'native-format-evidence.json', {})
    rebuild = read(PROGRESS / 'libvlc-rebuild.json', None)
    previews = []
    for preview in (rebuild or {}).get('previews', []):
        path = (ROOT / preview['path']).resolve()
        if path.is_relative_to(ROOT) and path.is_file() and path.stat().st_size <= 3 * 1024 * 1024:
            content = path.read_bytes()
            if content.startswith(b'\x89PNG\r\n\x1a\n'):
                previews.append({**preview, 'image': 'data:image/png;base64,' + base64.b64encode(content).decode()})
    for item in plan['items']:
        item['source_exists'] = (ROOT / item['source']).is_file()
    baseline = []
    for name, filename in [('Playback and local files','local-video-final.log'), ('Native fullscreen','fullscreen-final.log'),
                           ('Matroska integration','matroska-final.log'), ('FLAC exact samples','flac-final.log')]:
        log = ROOT / 'build/tests' / filename
        if log.exists():
            passed = [line.strip() for line in log.read_text(errors='replace').splitlines() if line.startswith('PASS:')]
            if passed: baseline.append({'name': name, 'build': plan.get('baseline_build'), 'log': str(log), 'checks': passed})
    data = {'generated_at': now(), 'source_digest': source_digest, 'built_version': version,
            'scope': catalog.get('scope'), 'root': str(ROOT), 'entries': catalog['entries'],
            'windows_entries': windows_catalog['entries'], 'windows_scope': windows_catalog.get('scope'),
            'work': plan['items'], 'checks': list(records.values()), 'baseline': baseline,
            'history': history[-20:][::-1], 'baseline_count': plan.get('baseline_inventory_count', len(catalog['entries'])),
            'native_evidence': evidence.get('verified_formats', []),
            'rebuild': rebuild, 'previews': previews,
            'native_notes': evidence.get('not_verified', []), 'native_as_of': evidence.get('generated_at')}
    atomic(PROGRESS / 'snapshot.json', json.dumps(data, indent=2) + '\n')
    rendered = CANVAS.replace('__SNAPSHOT__', json.dumps(data, ensure_ascii=True).replace('<', '\\u003c'))
    output = ROOT / 'build/progress-canvas.rendered'
    atomic(output, rendered)
    if CANVAS_TARGET is not None:
        target = Path(CANVAS_TARGET).expanduser().resolve()
        if target.suffixes[-2:] != ['.canvas', '.tsx'] or target.parent.name != 'canvases':
            raise RuntimeError('Canvas output must be a .canvas.tsx file in a managed canvases folder.')
        atomic(target, rendered)
    print(f'Updated local progress snapshot: {output}')
    return output

def run_check(identifier):
    script = ROOT / CHECKS[identifier]
    if not script.is_file():
        raise RuntimeError(f'{script.name} has not been implemented yet; no check was run.')
    started = time.monotonic()
    log = ROOT / 'build/progress-logs' / f'{identifier}-{time.time_ns()}.log'
    log.parent.mkdir(parents=True, exist_ok=True)
    scope = 'windows' if identifier.startswith('windows-') else 'macos'
    record = {'id': identifier, 'script': script.name, 'started_at': now(), 'status': 'running',
              'source_scope': scope, 'source_digest': digest(scope), 'log': str(log), 'summary': []}
    update_check(identifier, record); snapshot()
    try:
        with log.open('w') as stream:
            process = subprocess.run(['bash', str(script)], cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT)
        record['exit_code'] = process.returncode
        record['status'] = 'passed' if process.returncode == 0 else 'failed'
    except BaseException as error:
        record['status'] = 'interrupted'; record['error'] = str(error)
        raise
    finally:
        record['finished_at'] = now(); record['duration_seconds'] = round(time.monotonic() - started, 2)
        lines = log.read_text(errors='replace').splitlines() if log.exists() else []
        record['summary'] = [line[:500] for line in lines if line.startswith(('PASS', 'FAIL', 'NOT VERIFIED', 'UNVERIFIED'))][-10:]
        record['source_changed_during_check'] = record['source_digest'] != digest(scope)
        update_check(identifier, record); snapshot()
    print(f'{identifier}: {record["status"]}; log: {log}')
    return record['exit_code']

CANVAS = r'''import { Stack, Row, Grid, H1, H2, Text, Pill, Stat, Card, CardHeader, CardBody, Table, TextInput, Select, Button, Divider, useCanvasState, useHostTheme, useCanvasAction } from "cursor/canvas";

type Entry = { id:string; name:string; status:string; category:string; coverage:string; reference:string };
type Work = { id:string; name:string; implementation:string; stage:string; detail:string; next:string; source:string; source_exists:boolean };
type Check = { id:string; status:string; started_at:string; duration_seconds?:number; log:string; summary?:string[]; matches_current_source:boolean; source_scope?:string; source_changed_during_check?:boolean };
type Rebuild = {as_of:string; branch:string; platforms:{name:string;playback:string;sharing:string;distribution:string;evidence:string}[]; next:string[]};
type Preview = {id:string; name:string; path:string; caption:string; image:string};
type Snapshot = { generated_at:string; root:string; built_version:string|null; entries:Entry[]; windows_entries:Entry[]; work:Work[]; checks:Check[]; history:Check[]; baseline_count:number; baseline:{name:string; log:string; checks:string[]}[]; native_evidence:{name:string;container:string;frames:number}[]; native_as_of:string|null; rebuild:Rebuild|null; previews:Preview[] };
const snapshot = __SNAPSHOT__ as unknown as Snapshot;
const names: Record<string,string> = { independent: "Luma decoder", system: "Verified system route", experimental: "Partial", unimplemented: "Missing", unverified: "Unverified" };
const stages: Record<string,string> = { planned: "Queued", implementation: "Implementing", verification: "Verifying", integrated: "Integrated" };
function date(value: string) { return new Date(value).toLocaleString("en-US", { timeZone:"America/New_York", month:"short", day:"numeric", hour:"numeric", minute:"2-digit", timeZoneName:"short" }); }
function tone(status: string): "success"|"warning"|"info"|"neutral" { return status==="passed"?"success":status==="failed"?"warning":status==="running"?"info":"neutral"; }

export default function LumaCodecProgress() {
  const theme = useHostTheme();
  const dispatch = useCanvasAction();
  const [query,setQuery] = useCanvasState("luma-codecs-query", "");
  const [filter,setFilter] = useCanvasState("luma-codecs-filter", "pending");
  const [category,setCategory] = useCanvasState("luma-codecs-category", "all");
  const [detail,setDetail] = useCanvasState<string|null>("luma-codecs-detail", null);
  const [platform,setPlatform] = useCanvasState("luma-codecs-platform", "windows");
  const [previewId,setPreviewId] = useCanvasState("luma-modern-player-preview", "android-controls");
  const preview = snapshot.previews.find(p=>p.id===previewId) ?? snapshot.previews[0];
  const windows = platform === "windows";
  const entries = windows ? snapshot.windows_entries : snapshot.entries;
  const work = snapshot.work.filter(w => windows ? w.id.startsWith("windows-") : !w.id.startsWith("windows-"));
  const count = (status:string) => entries.filter(e=>e.status===status).length;
  const pending = entries.filter(e=>!["independent","system"].includes(e.status)).length;
  const checks = snapshot.checks.filter(c => windows ? c.source_scope === "windows" : c.source_scope !== "windows");
  const history = snapshot.history.filter(c => windows ? c.source_scope === "windows" : c.source_scope !== "windows");
  const rows = entries.filter(e=>(category==="all"||e.category===category) && (filter==="all" || (filter==="pending" ? !["independent","system"].includes(e.status) : e.status===filter)) && `${e.name} ${e.coverage}`.toLowerCase().includes(query.toLowerCase()));
  const selected = entries.find(e=>e.id===detail);
  const open = (path:string) => dispatch({type:"openFile",path:path.startsWith("/")?path:`${snapshot.root}/${path}`});
  const native = snapshot.native_evidence;
  return <Stack gap={22} style={{padding:24,maxWidth:1400,margin:"0 auto",color:theme.text.primary,background:theme.bg.editor}}>
    <Row justify="space-between" wrap><div><Text size="small" tone="tertiary">SDAS / LUMA PLAYER</Text><H1>Luma player review</H1></div><Pill>Local review</Pill></Row>
    {preview && <Stack gap={12}>
      <Row justify="space-between" wrap><H2>Review the app interfaces</H2><Select value={preview.id} onChange={setPreviewId} options={snapshot.previews.map(p=>({value:p.id,label:p.name}))}/></Row>
      <img src={preview.image} alt={preview.name} style={{width:"100%",maxHeight:600,objectFit:"contain",background:theme.bg.editor}}/>
      <Text size="small" tone="secondary">{preview.caption}</Text>
      <Row><Button variant="ghost" onClick={()=>open(preview.path)}>Open full image</Button><Text size="small" tone="tertiary">Source: native app test capture · original synthetic media only</Text></Row>
      <Divider/>
    </Stack>}
    {snapshot.rebuild && <Stack gap={12}>
      <Text>Mac and Windows now use libVLC. Android streams the desktop playlist over the local network. The original-codec inventory below records earlier experiments; it does not measure this backend's compatibility.</Text>
      <Table headers={["Platform","Playback evidence","Local sharing","Distribution / review"]} rows={snapshot.rebuild.platforms.map(p=>[
        <div><Text weight="medium">{p.name}</Text><Button variant="ghost" onClick={()=>open(p.evidence)}>Open evidence</Button></div>,p.playback,p.sharing,p.distribution
      ])}/>
      <Text size="small" tone="tertiary">Source: local native test logs and CI evidence · {snapshot.rebuild.branch} · recorded {date(snapshot.rebuild.as_of)}. Synthetic fixtures do not establish every VLC format or physical audio output.</Text>
      <H2>Next steps</H2>
      {snapshot.rebuild.next.map(item=><Text key={item} size="small">{item}</Text>)}
      <Divider/>
    </Stack>}
    <H2>Historical original-codec work</H2>
    <Row justify="space-between" wrap><Select value={platform} onChange={setPlatform} options={[{value:"windows",label:"Windows preview"},{value:"macos",label:"macOS alpha"}]}/><Pill>{work.filter(w=>w.implementation==="Original decoder"&&w.stage==="integrated").length} original decoder milestones integrated</Pill>{!windows && <Pill>{native.length} native fixture routes verified</Pill>}</Row>
    <Text tone="secondary">{pending} tracked groups still need work. Full VLC compatibility is not complete. Counts represent grouped formats, not an exact codec count or a parity percentage.</Text>
    <Grid columns="repeat(auto-fit, minmax(140px, 1fr))" gap={20}>
      <Stat value={count("independent")} label="Original implementations"/>
      <Stat value={count("system")} label={windows ? "Verified Windows routes" : "macOS playback routes"}/>
      <Stat value={count("experimental")} label="Partial support" tone="warning"/>
      <Stat value={count("unimplemented")+count("unverified")} label="Missing or unverified"/>
    </Grid>
    <Text size="small" tone="tertiary">{windows ? "Windows preview" : "macOS alpha"} working-tree format catalog · {entries.length} non-exhaustive groups · snapshot {date(snapshot.generated_at)}. Software decoding, container handling and audible hardware output are verified separately. Refreshed when local progress is recorded; this is not a background worker.</Text>
    {!windows && entries.length!==snapshot.baseline_count && <Text size="small" tone="secondary">Inventory expanded from {snapshot.baseline_count} to {entries.length} groups as broad families were separated and additional formats were tracked. Pending totals are not directly comparable to the earlier grouping.</Text>}
    <Divider/>
    <H2>Current development</H2>
    <Table headers={["Work item","Implementation","Stage","Next verification"]} framed={false} rows={work.map(w=>[
      <Button variant="ghost" disabled={!w.source_exists} onClick={()=>open(w.source)}>{w.name}</Button>,w.implementation,<Pill size="sm">{stages[w.stage]??w.stage}</Pill>,<div><Text size="small">{w.detail}</Text><Text size="small" tone="secondary">{w.next}</Text></div>
    ])}/>
    <Grid columns="repeat(auto-fit, minmax(280px, 1fr))" gap={22}>
      <Stack gap={10}>
        <H2>Recorded checks</H2>
        {checks.length>0 && <Table headers={["Check","Result","Evidence"]} rowTone={checks.map(c=>tone(c.status))} rows={checks.map(c=>[
          c.id,<div><Pill size="sm">{c.status}</Pill><Text size="small" tone="secondary">{c.source_scope === "windows" ? "Windows" : "macOS"} · {c.matches_current_source?"current source set":"earlier source set"}{c.source_changed_during_check?" · source changed during run":""}</Text>{c.summary?.filter(s=>s.startsWith("UNVERIFIED:")).slice(0,2).map(s=><Text key={s} size="small" tone="secondary">{s}</Text>)}</div>,
          <div><Text size="small" tone="secondary">{date(c.started_at)}{typeof c.duration_seconds==="number"?` · ${c.duration_seconds}s`:""}</Text><Button variant="ghost" onClick={()=>open(c.log)}>Open log</Button></div>
        ])}/>}
        {history.length>0 && <Card collapsible defaultOpen={false}><CardHeader>Recent completed attempts</CardHeader><CardBody><Table framed={false} headers={["Check","Result","Time / log"]} rows={history.map(c=>[c.id,c.status,<Button variant="ghost" onClick={()=>open(c.log)}>{date(c.started_at)}</Button>])}/></CardBody></Card>}
        {!windows && snapshot.baseline.length>0 && <Card collapsible defaultOpen={false}><CardHeader>Previously verified baseline 0.5.0</CardHeader><CardBody><Stack gap={10}>{snapshot.baseline.map(b=><div key={b.name}><Row justify="space-between"><Text size="small" weight="medium">{b.name}</Text><Button variant="ghost" onClick={()=>open(b.log)}>Log</Button></Row><Text size="small" tone="secondary">{b.checks.length} recorded assertions · historical evidence, not a fresh check of current changes</Text></div>)}</Stack></CardBody></Card>}
      </Stack>
      <Stack gap={10}><H2>Verification rules</H2><Text size="small">An implementation stays partial until valid bitstreams, malformed input, timing, seeking and player integration are checked. Native decoding is shown separately from an original Luma decoder.</Text><Text size="small" tone="secondary">The catalog is not exhaustive. Broad groups can contain both an implemented subset and missing related codecs. Hardware flags and plugin names are not playback proof.</Text>
        {!windows && native.length>0 && <Card collapsible defaultOpen={false}><CardHeader>{`${native.length} native fixture routes verified`}</CardHeader><CardBody><Stack gap={10}>{native.map(n=><div key={n.name}><Text size="small" weight="medium">{n.name}</Text><Text size="small" tone="secondary">{n.container} · {n.frames} decoded frames · native seek comparison passed. Not an original decoder.</Text></div>)}<Text size="small" tone="tertiary">Source: native-format-evidence.json · {snapshot.native_as_of?date(snapshot.native_as_of):"recorded fixture run"}. Generated 8-bit sources do not establish high-bit-depth or alpha conformance.</Text></Stack></CardBody></Card>}
        <Row wrap><Button variant="secondary" onClick={()=>open(windows ? "Windows/CodecSupport.json" : "CodecSupport.json")}>Support catalog</Button><Button variant="ghost" onClick={()=>open("Progress/work-items.json")}>Work record</Button></Row>
      </Stack>
    </Grid>
    <Divider/>
    <Row justify="space-between" wrap><H2>Format inventory</H2><Text size="small" tone="tertiary">Source: {windows ? "Windows/CodecSupport.json" : "CodecSupport.json"} · working tree</Text></Row>
    <Row wrap><TextInput value={query} onChange={setQuery} placeholder="Search formats or limitations" style={{minWidth:240,flex:1}}/><Select value={filter} onChange={setFilter} options={[{value:"pending",label:"Needs work"},{value:"all",label:"All support states"},...Object.entries(names).map(([value,label])=>({value,label}))]}/><Select value={category} onChange={setCategory} options={[{value:"all",label:"All categories"},...["Video","Audio","Subtitles","Container"].map(value=>({value,label:value}))]}/></Row>
    {rows.length>0 && <Table headers={["Format group","Status","Actual coverage"]} rows={rows.map(e=>[
      <Button variant="ghost" onClick={()=>setDetail(detail===e.id?null:e.id)}>{e.name}</Button>,<Pill size="sm">{names[e.status]??e.status}</Pill>,<Text size="small">{e.coverage}</Text>
    ])}/>}
    {selected && <Card><CardHeader>{selected.name}</CardHeader><CardBody><Stack gap={10}><Text>{selected.coverage}</Text><Text size="small" tone="secondary">{selected.category} · {names[selected.status]??selected.status}</Text><Row><Button variant="ghost" onClick={()=>setDetail(null)}>Close details</Button>{(selected.reference.startsWith("Sources/") || selected.reference.startsWith("Windows/")) && <Button variant="secondary" onClick={()=>open(selected.reference)}>Open implementation</Button>}</Row></Stack></CardBody></Card>}
  </Stack>;
}
'''

def main():
    global CANVAS_TARGET
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--canvas', help='Also rewrite this managed local canvas after each progress change.')
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('refresh')
    check = commands.add_parser('check'); check.add_argument('id', choices=CHECKS)
    arguments = parser.parse_args()
    CANVAS_TARGET = arguments.canvas
    return run_check(arguments.id) if arguments.command == 'check' else (snapshot() and 0)

if __name__ == '__main__':
    raise SystemExit(main())
