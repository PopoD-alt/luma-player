#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(30, 600)][int]$SmokeTimeoutSeconds = 180
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
    $packages = @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.NETCore.App.Host.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')
    $resolved = @()
    foreach ($name in $packages) {
        $pack = @($items | Where-Object { $_['NuGetPackageId'] -eq $name -or $_['Identity'] -eq $name }) | Select-Object -First 1
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

function Invoke-VisualSmoke([string]$Executable) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $publishRoot
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
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
    }
    finally { $process.Dispose() }
}

Push-Location $windowsRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
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
    Invoke-Dotnet @('restore', $testsProject)
    Invoke-Dotnet @('run', '--project', $testsProject, '--configuration', $Configuration, '--no-restore', '--', $fixturesRoot)
    Invoke-Dotnet @('restore', $appProject, '--runtime', 'win-x64', '-p:SelfContained=true', '-p:PublishSingleFile=true')
    Invoke-Dotnet @('publish', $appProject, '--configuration', $Configuration, '--runtime', 'win-x64', '--self-contained', 'true', '--no-restore',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false', '--output', $publishRoot)
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
    Invoke-VisualSmoke $executable
    $metadata = [ordered]@{
        distribution = 'unsigned-windows-review'; sdk = $actualSdk; runtimeIdentifier = 'win-x64'; configuration = $Configuration
        commit = $env:GITHUB_SHA; operatingSystem = [Environment]::OSVersion.VersionString
        coreTestsPassed = $true; visualSmokePassed = $true; runtimePackages = $runtimePackages
        createdUtc = [DateTime]::UtcNow.ToString('o')
    }
    $metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $publishRoot 'BUILD-INFO.json') -Encoding utf8
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
    foreach ($file in @($archive, $pendingArchive, ($archive + '.sha256'), ($archive + '.sha256.pending'))) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
    throw
}
finally { Pop-Location }
