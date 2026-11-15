#requires -Version 7.0
# Original synthetic approval-gate tests. No runtime DLL is loaded or installed.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$build = Join-Path $PSScriptRoot 'build.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($build, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw ($parseErrors | Out-String) }
# Exercise the actual build functions without invoking a Windows publish or its
# regenerable-output cleanup. This also runs on a portable PowerShell host.
foreach ($name in @('Invoke-CentralRuntimeVerification', 'Resolve-ApprovedBundle')) {
    $definition = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $false)
    if (@($definition).Count -ne 1) { throw "Missing unique build function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$python = Get-Command python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $python) { $python = Get-Command python3 -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 }
if ($null -eq $python) { throw 'Python 3 is required for synthetic central-verifier tests.' }
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('Luma-bundle-build-test-' + [Guid]::NewGuid().ToString('N'))
$repoRoot = Join-Path $temporary 'original-test-repository'
$artifactsRoot = Join-Path $repoRoot 'Windows/artifacts'
$BundledRuntimeRoot = Join-Path $temporary 'prepared-component'
$AcquireTestVlc = $false
New-Item -ItemType Directory -Path (Join-Path $repoRoot 'Scripts') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot 'Scripts/prepare-libvlc.py') -Destination (Join-Path $repoRoot 'Scripts/prepare-libvlc.py')
$fixture = @'
import hashlib,json,sys
from pathlib import Path
repo=Path(sys.argv[1]);root=Path(sys.argv[2]);variant=sys.argv[3]
root.mkdir(parents=True,exist_ok=True)
package='windows-original-bundle-test' if variant!='package-injection' else 'windows-original;injection'
files={}
for name in ['libvlc.dll','libvlccore.dll','plugins/codec/original.dll','Sources/CorrespondingSource.tar','Notices/COPYING.LIB']:
 p=root/name;p.parent.mkdir(parents=True,exist_ok=True);data=('Original test bytes: '+name).encode();p.write_bytes(data)
 files[name]={'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()}
source=files['Sources/CorrespondingSource.tar']['sha256']
artifact='1'*64
manifest={'schema':1,'package':package,'artifactSha256':artifact,'developmentOnly':variant=='development','redistributionApproved':variant!='development','files':files}
if variant=='string-approval':manifest['redistributionApproved']='true'
(root/'luma-libvlc-runtime.json').write_text(json.dumps(manifest,indent=2)+'\n')
record={'artifact':{'sha256':artifact},'runtimeEntry':'libvlc.dll','runtimeInventorySha256':hashlib.sha256(json.dumps(files,sort_keys=True,separators=(',',':')).encode()).hexdigest(),
 'correspondingSource':{'path':'Sources/CorrespondingSource.tar','sha256':source},
 'audit':{'status':'approved-for-redistribution','moduleLicensesReviewed':True,'dependencyLicensesReviewed':True,'completeCorrespondingSources':True,'replacementMechanismVerified':variant!='missing-gate'}}
registry=repo/'ThirdParty/libvlc/packages.json';registry.parent.mkdir(parents=True,exist_ok=True);registry.write_text(json.dumps({'packages':{package:record}}))
if variant=='changed-plugin':
 p=root/'plugins/codec/original.dll';b=bytearray(p.read_bytes());b[0]^=1;p.write_bytes(b)
'@
function Initialize-Fixture([string]$Variant) {
    & $python.Source -c $fixture $repoRoot $BundledRuntimeRoot $Variant
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create the original synthetic component fixture.' }
}
function Reject-Approval([string]$Label) {
    $rejected = $false
    try { [void](Resolve-ApprovedBundle) } catch { $rejected = $true }
    if (-not $rejected) { throw "Bundle build accepted $Label" }
}
try {
    Initialize-Fixture 'valid'
    $pins = Resolve-ApprovedBundle
    if ($pins.Package -cne 'windows-original-bundle-test' -or
        $pins.ManifestSha256 -cne (Get-FileHash -LiteralPath (Join-Path $BundledRuntimeRoot 'luma-libvlc-runtime.json') -Algorithm SHA256).Hash.ToLowerInvariant() -or
        $pins.SourceSha256 -cne (Get-FileHash -LiteralPath (Join-Path $BundledRuntimeRoot 'Sources/CorrespondingSource.tar') -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'The build did not derive exact package/manifest/source pins.'
    }
    foreach ($variant in @('development', 'missing-gate', 'changed-plugin', 'package-injection', 'string-approval')) {
        Initialize-Fixture $variant; Reject-Approval $variant
    }
    # Exercise the actual top-level try/catch with an invalid component. All
    # output paths are synthetic: a failed preflight must preserve prior output.
    Initialize-Fixture 'missing-gate'
    $windowsRoot = Join-Path $repoRoot 'Windows'
    $publishRoot = Join-Path $artifactsRoot 'win-x64'; $reportsRoot = Join-Path $artifactsRoot 'review'
    $archive = Join-Path $artifactsRoot 'original-review.zip'; $pendingArchive = $archive + '.pending'
    $outputsInitialized = $false; $bundle = $null
    New-Item -ItemType Directory -Path $publishRoot, $reportsRoot -Force | Out-Null
    $sentinel = Join-Path $publishRoot 'prior-owner-folder.txt'
    [IO.File]::WriteAllText($sentinel, 'Original prior output'); [IO.File]::WriteAllText($archive, 'Original prior archive')
    $mainTry = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })
    if ($mainTry.Count -ne 1) { throw 'Cannot identify the actual build preflight/cleanup block.' }
    $failed = $false
    Push-Location $windowsRoot
    try { . ([scriptblock]::Create($mainTry[0].Extent.Text)) } catch { $failed = $true }
    if (-not $failed -or -not (Test-Path -LiteralPath $sentinel) -or -not (Test-Path -LiteralPath $archive) -or $outputsInitialized) {
        throw 'A rejected bundled component changed prior build outputs.'
    }
    Initialize-Fixture 'valid'
    $AcquireTestVlc = $true; Reject-Approval 'bundled plus stock acquisition'; $AcquireTestVlc = $false
    $savedRoot = $BundledRuntimeRoot; $BundledRuntimeRoot = 'relative/component'; Reject-Approval 'relative component'; $BundledRuntimeRoot = $savedRoot
    $savedOutputs = $artifactsRoot
    foreach ($overlap in @($BundledRuntimeRoot, (Split-Path -Parent $BundledRuntimeRoot), (Join-Path $BundledRuntimeRoot 'nested-output'))) {
        $artifactsRoot = $overlap; Reject-Approval 'component/output overlap'
    }
    $artifactsRoot = $savedOutputs
    Write-Host 'PASS: actual PowerShell bundle functions and central verifier bind exact pins; development, missing audit, changed module, unsafe package, conflicting stock acquisition, relative paths and output overlap fail closed; rejected preflight preserves prior folder/archive'
}
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force } }
