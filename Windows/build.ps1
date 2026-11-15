#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(30, 600)][int]$SmokeTimeoutSeconds = 600,
    [switch]$AcquireTestVlc,
    [string]$BundledRuntimeRoot,
    [switch]$SkipReviewZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The WPF review build and visual smoke test must run on Windows.' }
if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'This review script targets a native x64 Windows machine. ARM64 is not verified.'
}

$requiredSdk = '10.0.404'
$windowsRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $windowsRoot
$artifactsRoot = Join-Path $windowsRoot 'artifacts'
$publishRoot = Join-Path $artifactsRoot 'win-x64'
$reportsRoot = Join-Path $artifactsRoot 'review'
$smokeRoot = Join-Path $reportsRoot 'smoke'
$archive = Join-Path $artifactsRoot 'Luma-Player-windows-x64-review.zip'
$pendingArchive = $archive + '.pending'
$fixturesRoot = Join-Path $repoRoot 'Tests/Fixtures'
$appProject = Join-Path $windowsRoot 'Luma.Windows/Luma.Windows.csproj'
$testsProject = Join-Path $windowsRoot 'Luma.Core.Tests/Luma.Core.Tests.csproj'
$bundle = $null
$outputsInitialized = $false

function Invoke-CentralRuntimeVerification([string]$RuntimeRoot) {
    $python = Get-Command python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $python) { $python = Get-Command python3 -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if ($null -eq $python) { throw 'Python 3 is required for the central approved-component verifier.' }
    $verifier = Join-Path $repoRoot 'Scripts/prepare-libvlc.py'
    $lines = @(& $python.Source $verifier verify --runtime $RuntimeRoot --distribution 2>&1)
    if ($LASTEXITCODE -ne 0) { throw ('Central runtime approval failed: ' + ($lines -join [Environment]::NewLine)) }
    $verified = ($lines -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable
    if ($verified -isnot [System.Collections.IDictionary] -or $verified['status'] -cne 'verified' -or
        $verified['redistributionApproved'] -ne $true -or $verified['developmentOnly'] -ne $false) {
        throw 'The central verifier did not return an approved distribution component.'
    }
    return $verified
}

function Resolve-ApprovedBundle {
    if ($AcquireTestVlc) { throw '-BundledRuntimeRoot cannot be combined with -AcquireTestVlc.' }
    if (-not [IO.Path]::IsPathFullyQualified($BundledRuntimeRoot)) { throw '-BundledRuntimeRoot must be an absolute centrally prepared runtime folder.' }
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($BundledRuntimeRoot))
    $outputs = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($artifactsRoot))
    $separator = [IO.Path]::DirectorySeparatorChar
    if ($root.Equals($outputs, [StringComparison]::OrdinalIgnoreCase) -or
        $root.StartsWith($outputs + $separator, [StringComparison]::OrdinalIgnoreCase) -or
        $outputs.StartsWith($root + $separator, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The approved source component must not overlap the regenerable Windows/artifacts folder.'
    }
    # This registry-backed approval is deliberately performed before clearing or
    # creating any app/review output. A self-declared local manifest is insufficient.
    $verified = Invoke-CentralRuntimeVerification $root
    $canonicalRoot = $verified['runtimeRoot']
    if ($canonicalRoot -isnot [string] -or -not [IO.Path]::IsPathFullyQualified($canonicalRoot)) { throw 'The verifier did not identify a canonical runtime folder.' }
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($canonicalRoot))
    if ($root.Equals($outputs, [StringComparison]::OrdinalIgnoreCase) -or
        $root.StartsWith($outputs + $separator, [StringComparison]::OrdinalIgnoreCase) -or
        $outputs.StartsWith($root + $separator, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The resolved approved component must not overlap the regenerable Windows/artifacts folder.'
    }
    $manifestPath = Join-Path $root 'luma-libvlc-runtime.json'
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 4MB) { throw 'The approved manifest exceeds its size limit.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $package = $manifest['package']
    if ($package -isnot [string] -or $package -cnotmatch '^windows-[a-z0-9][a-z0-9.-]{0,119}$' -or
        $verified['package'] -cne $package -or ($manifest['schema'] -isnot [int] -and $manifest['schema'] -isnot [long]) -or $manifest['schema'] -ne 1 -or
        $manifest['redistributionApproved'] -isnot [bool] -or $manifest['redistributionApproved'] -ne $true -or
        $manifest['developmentOnly'] -isnot [bool] -or $manifest['developmentOnly'] -ne $false) {
        throw 'The approved component does not describe a supported Windows package.'
    }
    return @{
        Root = $root; Package = $package
        ManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        SourceSha256 = (Get-FileHash -LiteralPath (Join-Path $root 'Sources/CorrespondingSource.tar') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath (Join-Path $reportsRoot 'dotnet.log') -Append
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

function Copy-NoticeFiles([string]$Source, [string]$Destination) {
    $files = @(Get-ChildItem -LiteralPath $Source -File -Recurse | Where-Object {
        $_.Name -match '^(?i:LICENSE(?:\..*)?|NOTICE(?:S)?(?:\..*)?|THIRD[-_]?PARTY[-_]?NOTICES(?:\..*)?)$' -or
        $_.Extension -eq '.nuspec' -or $_.Name -match '(?i)\.versions\.txt$' -or
        $_.FullName -match '[/\\]_licenses[/\\]'
    })
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    return $files.Count
}

function Copy-RuntimeNotices {
    $sdkLines = @(& dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the installed SDK notices.' }
    $sdkDirectory = $null
    foreach ($line in $sdkLines) {
        if ($line -match ('^' + [regex]::Escape($requiredSdk) + ' \[(.+)\]$')) {
            $sdkDirectory = Join-Path $Matches[1] $requiredSdk; break
        }
    }
    if ($null -eq $sdkDirectory) { throw 'The pinned SDK directory could not be resolved.' }
    # SDK-installed apphost packs may omit package-local notices. Preserve their
    # exact SDK distribution's license/notices instead of fetching upstream main.
    $dotnetDirectory = Split-Path -Parent (Split-Path -Parent $sdkDirectory)
    $sdkLicenseRoot = Join-Path (Join-Path $publishRoot 'Licenses/Microsoft/SDK-distribution') $requiredSdk
    New-Item -ItemType Directory -Path $sdkLicenseRoot -Force | Out-Null
    foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $file = Join-Path $dotnetDirectory $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing pinned SDK distribution notice: $name." }
        Copy-Item -LiteralPath $file -Destination (Join-Path $sdkLicenseRoot $name)
    }
    # Resolve actual build inputs, whether restored from NuGet or preinstalled in
    # dotnet/packs. A restore download list alone omits SDK-installed host packs.
    $resolutionArguments = @('msbuild', $appProject, '-target:ResolveFrameworkReferences', '-nologo', '-verbosity:quiet',
        '-p:RuntimeIdentifier=win-x64', '-p:SelfContained=true', '-p:PublishSingleFile=true',
        '-getItem:ResolvedRuntimePack,ResolvedAppHostPack,ResolvedSingleFileHostPack')
    $resolution = @(& dotnet @resolutionArguments)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve runtime inputs for the notice inventory.' }
    $details = ($resolution -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable
    $items = @($details.Items.ResolvedRuntimePack) + @($details.Items.ResolvedAppHostPack) + @($details.Items.ResolvedSingleFileHostPack)
    # SDK-installed host items have Identity=AppHost/SingleFileHost and a resolved
    # PackageDirectory/Path, but omit NuGetPackageId/NuGetPackageVersion. Retain
    # a path-free diagnostic inventory so CI can show that distinction safely.
    $diagnostics = @()
    foreach ($kind in @('ResolvedRuntimePack', 'ResolvedAppHostPack', 'ResolvedSingleFileHostPack')) {
        foreach ($item in $details.Items[$kind]) {
            $diagnostics += @{
                itemKind = $kind; identity = $item['Identity']; nuGetPackageId = $item['NuGetPackageId']
                nuGetPackageVersion = $item['NuGetPackageVersion']; runtimeIdentifier = $item['RuntimeIdentifier']
                hasPackageDirectory = -not [string]::IsNullOrEmpty($item['PackageDirectory'])
                hasResolvedPath = -not [string]::IsNullOrEmpty($item['Path']); pathInPackage = $item['PathInPackage']
            }
        }
    }
    $diagnostics | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportsRoot 'runtime-pack-resolution.json') -Encoding utf8
    $packages = @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.NETCore.App.Host.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')
    $resolved = @()
    foreach ($name in $packages) {
        $directoryIdentity = '(?i)[/\\]' + [regex]::Escape($name) + '[/\\][0-9.]+(?:[/\\]|$)'
        $pack = @($items | Where-Object {
            $_['NuGetPackageId'] -eq $name -or $_['Identity'] -eq $name -or
            $_['PackageDirectory'] -match $directoryIdentity -or $_['Path'] -match $directoryIdentity
        }) | Select-Object -First 1
        if ($null -eq $pack) { throw "Cannot identify the resolved runtime pack: $name." }
        $source = $pack['PackageDirectory']
        if ([string]::IsNullOrEmpty($source)) { $source = $pack['Path'] }
        $match = [regex]::Match($source, ('(?i)^(.+[/\\]' + [regex]::Escape($name) + '[/\\](?<version>[0-9.]+))(?:[/\\]|$)'))
        if (-not $match.Success) { throw "Cannot resolve the runtime package directory/version: $name." }
        $source = $match.Groups[1].Value
        $version = $match.Groups['version'].Value
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Resolved runtime package is missing: $name/$version." }
        $target = Join-Path (Join-Path (Join-Path $publishRoot 'Licenses/Microsoft') $name) $version
        $copied = Copy-NoticeFiles $source $target
        $resolved += @{ name = $name; version = $version; packageNoticeFiles = $copied; sdkDistributionNotices = $requiredSdk }
    }
    # Some Desktop runtime packages contain LICENSE but no separate WPF notices.
    # Keep the matching installed SDK's verbatim Desktop notices as well.
    $desktopSdk = Join-Path $sdkDirectory 'Sdks/Microsoft.NET.Sdk.WindowsDesktop'
    $sdkNotices = Join-Path (Join-Path $publishRoot 'Licenses/Microsoft/WindowsDesktop-SDK') $requiredSdk
    foreach ($name in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        $file = Join-Path $desktopSdk $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing pinned Desktop SDK notice: $name." }
        New-Item -ItemType Directory -Path $sdkNotices -Force | Out-Null
        Copy-Item -LiteralPath $file -Destination (Join-Path $sdkNotices $name)
    }
    return $resolved
}

# Stock VideoLAN VLC is acquired only for an explicitly requested developer/CI
# test. It is never copied into the app publish directory or redistribution ZIP.
function Resolve-TestVlcRuntime {
    $runtime = $env:LUMA_LIBVLC_PATH
    if (-not [string]::IsNullOrWhiteSpace($runtime)) {
        if (-not [IO.Path]::IsPathFullyQualified($runtime) -or -not (Test-Path -LiteralPath $runtime -PathType Leaf)) {
            throw 'LUMA_LIBVLC_PATH must be an existing absolute libvlc.dll path.'
        }
    }
    elseif ($AcquireTestVlc) {
        $version = '3.0.24'
        $sha256 = 'fcf30850371ad10c9373cc4f0f4501e7dee49e3e9ae9f20c72fb2661a1ca6323'
        $cache = Join-Path $windowsRoot '.cache/vlc-test-runtime'
        $zip = Join-Path $cache "vlc-$version-win64.zip"
        $extracted = Join-Path $cache "vlc-$version"
        New-Item -ItemType Directory -Path $cache -Force | Out-Null
        if (-not (Test-Path -LiteralPath $zip -PathType Leaf) -or (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha256) {
            $download = $zip + '.pending'
            try {
                Invoke-WebRequest -Uri "https://download.videolan.org/pub/videolan/vlc/$version/win64/vlc-$version-win64.zip" -OutFile $download -TimeoutSec 180
                if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sha256) { throw 'The external VLC test runtime hash does not match the official pinned archive.' }
                Move-Item -LiteralPath $download -Destination $zip -Force
            }
            finally { if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force } }
        }
        if (Test-Path -LiteralPath $extracted) { Remove-Item -LiteralPath $extracted -Recurse -Force }
        Expand-Archive -LiteralPath $zip -DestinationPath $cache -Force
        $runtime = Join-Path $extracted 'libvlc.dll'
    }
    else {
        $runtime = Join-Path $env:ProgramFiles 'VideoLAN/VLC/libvlc.dll'
    }
    if (-not (Test-Path -LiteralPath $runtime -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $runtime) 'plugins') -PathType Container)) {
        throw 'Install official x64 VLC3 separately, set LUMA_LIBVLC_PATH, or explicitly use -AcquireTestVlc for CI/development only.'
    }
    $env:LUMA_LIBVLC_PATH = [IO.Path]::GetFullPath($runtime)
    @{ mode = 'external-test-runtime'; runtimeBundled = $false; approvedForRedistribution = $false;
        pinnedAcquisition = [bool]$AcquireTestVlc; officialArchiveVersion = '3.0.24';
        officialArchiveSha256 = 'fcf30850371ad10c9373cc4f0f4501e7dee49e3e9ae9f20c72fb2661a1ca6323' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportsRoot 'libvlc-runtime-gate.json') -Encoding utf8
}

function Invoke-VisualSmoke([string]$Executable) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $publishRoot
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $expectBundled = $null -ne $bundle
    $start.Environment['LUMA_EXPECT_BUNDLED_RUNTIME'] = $expectBundled.ToString().ToLowerInvariant()
    if ($expectBundled) {
        # An inherited developer override must never hide a broken bundled route.
        [void]$start.Environment.Remove('LUMA_LIBVLC_PATH')
        [void]$start.Environment.Remove('VLC_PLUGIN_PATH')
    }
    foreach ($argument in @('--smoke-test', $fixturesRoot, $smokeRoot)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'The Windows visual smoke process did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $finished = $process.WaitForExit($SmokeTimeoutSeconds * 1000)
        if (-not $finished) { $process.Kill($true); $process.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $reportsRoot 'smoke.stdout.log'), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $reportsRoot 'smoke.stderr.log'), $stderr.GetAwaiter().GetResult())
        if (-not $finished) { throw "Windows visual smoke test exceeded $SmokeTimeoutSeconds seconds." }
        if ($process.ExitCode -ne 0) { throw "Windows visual smoke test failed with exit code $($process.ExitCode)." }
        if (@(Get-ChildItem -LiteralPath $smokeRoot -Filter '*.png' -File -Recurse).Count -eq 0) {
            throw 'Visual smoke exited successfully but produced no review PNG.'
        }
        $resultsPath = Join-Path $smokeRoot 'smoke-results.json'
        if (-not (Test-Path -LiteralPath $resultsPath -PathType Leaf)) {
            throw 'Native smoke exited successfully but produced no smoke-results.json.'
        }
        $results = Get-Content -LiteralPath $resultsPath -Raw | ConvertFrom-Json -AsHashtable
        if ($results -isnot [System.Collections.IDictionary]) { throw 'Native smoke did not produce a result object.' }
        foreach ($required in @('libvlcPlayback', 'hevc10Aac51', 'hevc10Matroska', 'subtitleSelection', 'lifecycleRecovery', 'sharingUi')) {
            if (-not $results.Contains($required) -or $results[$required] -cne 'passed') { throw "Missing native libVLC verification: $required." }
        }
        if ($results['runtimeVersion'] -notmatch '^3\.' -or $results['runtimeBundled'] -ne $expectBundled) { throw 'The smoke process did not use the selected libVLC deployment route.' }
        $endpoints = $results['activeAudioRenderEndpoints']
        if ($null -ne $endpoints -and (($endpoints -isnot [int] -and $endpoints -isnot [long] -and $endpoints -isnot [uint32] -and $endpoints -isnot [uint64]) -or $endpoints -lt 0)) { throw 'Invalid native audio endpoint diagnostic.' }
        if ($results['physicalAudio'] -cnotin @('unverified-no-active-render-endpoint', 'system-output-requested-audibility-not-measured')) { throw 'Physical audio status must not invent an audible-output pass.' }
        if ($results['physicalAudio'] -ceq 'unverified-no-active-render-endpoint' -and ($null -eq $endpoints -or $endpoints -ne 0)) { throw 'Unverified audio-device status requires an observed zero endpoint count.' }
        return $results
    }
    finally { $process.Dispose() }
}

Push-Location $windowsRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    if (-not [string]::IsNullOrWhiteSpace($BundledRuntimeRoot)) { $bundle = Resolve-ApprovedBundle }
    $outputsInitialized = $true
    New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
    # These fixed folders contain only regenerable outputs owned by this script.
    foreach ($folder in @($publishRoot, $reportsRoot)) {
        if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    }
    foreach ($file in @($archive, $pendingArchive, ($archive + '.sha256'), ($archive + '.sha256.pending'))) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
    New-Item -ItemType Directory -Path $publishRoot, $smokeRoot -Force | Out-Null
    $actualSdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $requiredSdk) { throw "Install the pinned .NET SDK $requiredSdk; selected SDK: $actualSdk." }
    if (-not (Test-Path -LiteralPath (Join-Path $fixturesRoot 'flac/manifest.json'))) { throw 'Run from a complete source checkout containing Tests/Fixtures.' }
    if ($null -eq $bundle) { Resolve-TestVlcRuntime }
    else {
        @{ mode = 'approved-app-local-owner-review'; runtimeBundled = $true; approvedForRedistribution = $true
            package = $bundle.Package; manifestSha256 = $bundle.ManifestSha256; sourceBundleSha256 = $bundle.SourceSha256 } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportsRoot 'libvlc-runtime-gate.json') -Encoding utf8
    }
    Invoke-Dotnet @('restore', $testsProject)
    $env:LUMA_WINDOWS_MEDIA_EXPORT = Join-Path $reportsRoot 'generated'
    Invoke-Dotnet @('run', '--project', $testsProject, '--configuration', $Configuration, '--no-restore', '--', $fixturesRoot)
    Invoke-Dotnet @('restore', $appProject, '--runtime', 'win-x64', '-p:SelfContained=true', '-p:PublishSingleFile=true')
    $publishArguments = @('publish', $appProject, '--configuration', $Configuration, '--runtime', 'win-x64', '--self-contained', 'true', '--no-restore',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false', '--output', $publishRoot)
    if ($null -ne $bundle) {
        $publishArguments += @("-p:LumaBundledVlcPackage=$($bundle.Package)", "-p:LumaBundledVlcManifestSha256=$($bundle.ManifestSha256)", "-p:LumaBundledVlcSourceSha256=$($bundle.SourceSha256)")
    }
    Invoke-Dotnet $publishArguments
    if (@(Get-ChildItem -LiteralPath $publishRoot -Filter 'libvlc*.dll' -File -Recurse).Count -gt 0 -or (Test-Path -LiteralPath (Join-Path $publishRoot 'libvlc/plugins'))) { throw 'Unapproved VLC runtime must remain outside the publish artifact.' }
    if ($null -ne $bundle) {
        [void](Invoke-CentralRuntimeVerification $bundle.Root)
        $bundledTarget = Join-Path $publishRoot 'libvlc'
        Invoke-Dotnet @('run', '--project', $testsProject, '--configuration', $Configuration, '--no-build', '--', '--stage-owner-runtime',
            $bundle.Root, $bundledTarget, $bundle.Package, $bundle.ManifestSha256, $bundle.SourceSha256)
        [void](Invoke-CentralRuntimeVerification $bundledTarget)
        if ((Get-FileHash -LiteralPath (Join-Path $bundledTarget 'luma-libvlc-runtime.json') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $bundle.ManifestSha256) { throw 'The copied runtime manifest does not match the embedded app pin.' }
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $publishRoot 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $windowsRoot 'README.md') -Destination (Join-Path $publishRoot 'README-WINDOWS.md')
    foreach ($file in @('FORMAT_SUPPORT.md', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $windowsRoot $file) -Destination (Join-Path $publishRoot $file)
    }
    $runtimePackages = @(Copy-RuntimeNotices)
    $buildInfoRoot = Join-Path $publishRoot 'BuildInfo'
    New-Item -ItemType Directory -Path $buildInfoRoot -Force | Out-Null
    # Single-file publish embeds this metadata. Retain reference copies for review.
    $intermediateOutput = Join-Path $windowsRoot "Luma.Windows/bin/$Configuration/net10.0-windows/win-x64"
    foreach ($file in @('Luma Player.deps.json', 'Luma Player.runtimeconfig.json')) {
        $source = Join-Path $intermediateOutput $file
        if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination $buildInfoRoot }
    }
    $executable = Join-Path $publishRoot 'Luma Player.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The published WPF executable is missing.' }
    $smokeResults = Invoke-VisualSmoke $executable
    if ($null -ne $bundle) { [void](Invoke-CentralRuntimeVerification (Join-Path $publishRoot 'libvlc')) }
    $metadata = [ordered]@{
        distribution = 'unsigned-windows-owner-review'; sdk = $actualSdk; runtimeIdentifier = 'win-x64'; configuration = $Configuration
        commit = $env:GITHUB_SHA; operatingSystem = [Environment]::OSVersion.VersionString
        coreTestsPassed = $true; visualSmokePassed = $true; smokeResults = $smokeResults; runtimePackages = $runtimePackages
        runtimeBundled = $null -ne $bundle
        bundledComponent = $(if ($null -ne $bundle) { @{ package = $bundle.Package; manifestSha256 = $bundle.ManifestSha256; sourceBundleSha256 = $bundle.SourceSha256 } } else { $null })
        archiveCreated = -not [bool]$SkipReviewZip
        createdUtc = [DateTime]::UtcNow.ToString('o')
    }
    $metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $publishRoot 'BUILD-INFO.json') -Encoding utf8
    if ($SkipReviewZip) {
        $metadata['archiveCreated'] = $false
        $metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportsRoot 'review.json') -Encoding utf8
        Write-Host "Owner-review folder prepared: $publishRoot"
        Write-Host 'No review ZIP, installer, tag, release, upload, signing or feed update was created.'
        return
    }
    # Never expose a partially created ZIP to the workflow's always-upload step.
    [IO.Compression.ZipFile]::CreateFromDirectory($publishRoot, $pendingArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $pendingArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    ($hash + '  ' + [IO.Path]::GetFileName($archive)) | Set-Content -LiteralPath ($archive + '.sha256.pending') -Encoding ascii
    $metadata['archive'] = [IO.Path]::GetFileName($archive); $metadata['sha256'] = $hash
    $metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportsRoot 'review.json') -Encoding utf8
    Move-Item -LiteralPath $pendingArchive -Destination $archive
    Move-Item -LiteralPath ($archive + '.sha256.pending') -Destination ($archive + '.sha256')
    Write-Host "Review ZIP prepared: $archive"
    Write-Host 'Unsigned Windows review artifact only. No tag, release, upload, feed update or publication was performed.'
}
catch {
    if ($outputsInitialized) {
        foreach ($file in @($archive, $pendingArchive, ($archive + '.sha256'), ($archive + '.sha256.pending'))) {
            if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
        }
    }
    throw
}
finally { Pop-Location }
