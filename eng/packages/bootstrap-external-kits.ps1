[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$pinPath = Join-Path $PSScriptRoot 'external-kits.json'
$pins = Get-Content -LiteralPath $pinPath -Raw | ConvertFrom-Json
if ($pins.schemaVersion -ne 1 -or @($pins.kits).Count -ne 2) {
    throw 'The external kit pin manifest is invalid.'
}

$artifactsRoot = Join-Path $repoRoot 'artifacts'
$sourceRoot = Join-Path $artifactsRoot 'external-kits\sources'
$feedRoot = Join-Path $artifactsRoot 'local-packages'
$nugetConfig = Join-Path $artifactsRoot 'external-kits\NuGet.Config'
New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
New-Item -ItemType Directory -Path $feedRoot -Force | Out-Null

function Assert-WithinRoot([string]$Path, [string]$Root) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to modify a path outside the repository artifacts directory.'
    }
}

Assert-WithinRoot $sourceRoot $artifactsRoot
Assert-WithinRoot $feedRoot $artifactsRoot
Get-ChildItem -LiteralPath $feedRoot -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.nupkg', '.snupkg' } |
    Remove-Item -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($kit in $pins.kits) {
    if ($kit.commit -notmatch '^[0-9a-f]{40}$' -or
        $kit.packageId -notmatch '^[A-Za-z0-9.]+$' -or
        $kit.version -notmatch '^[0-9A-Za-z.+-]+$') {
        throw "Invalid exact pin for $($kit.name)."
    }

    $checkout = Join-Path $sourceRoot "$($kit.name)-$($kit.commit)"
    if (-not (Test-Path -LiteralPath $checkout)) {
        & git clone --quiet --no-checkout $kit.repository $checkout
        if ($LASTEXITCODE -ne 0) { throw "Could not clone $($kit.name)." }
    }
    $actualHead = (& git -C $checkout rev-parse HEAD 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualHead -cne $kit.commit) {
        & git -C $checkout fetch --quiet origin $kit.commit
        if ($LASTEXITCODE -ne 0) { throw "Could not fetch pinned commit for $($kit.name)." }
    }
    & git -C $checkout checkout --quiet --detach $kit.commit
    if ($LASTEXITCODE -ne 0) { throw "Could not check out pinned commit for $($kit.name)." }
    $actualHead = (& git -C $checkout rev-parse HEAD).Trim()
    if ($actualHead -cne $kit.commit) { throw "Pinned SHA mismatch for $($kit.name)." }

    $project = Join-Path $checkout ($kit.project -replace '/', '\')
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Package project is missing for $($kit.name)." }
    $packArgs = @(
        $project,
        '--configuration', 'Release',
        '--output', $feedRoot,
        "-p:PackageVersion=$($kit.version)",
        '-p:ContinuousIntegrationBuild=true'
    )
    & dotnet pack @packArgs
    if ($LASTEXITCODE -ne 0) { throw "Package build failed for $($kit.name)." }

    $packagePath = Join-Path $feedRoot "$($kit.packageId).$($kit.version).nupkg"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw "Expected package is missing for $($kit.name)." }
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $nuspecEntry = $archive.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
        if ($null -eq $nuspecEntry) { throw "Package metadata is missing for $($kit.name)." }
        $reader = [IO.StreamReader]::new($nuspecEntry.Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($nuspec.package.metadata.id -cne $kit.packageId -or $nuspec.package.metadata.version -cne $kit.version) {
            throw "Package ID/version mismatch for $($kit.name)."
        }
    }
    finally { $archive.Dispose() }
    Write-Host "$($kit.packageId)@$($kit.version) pinned=$($kit.commit) verified"
}

$escapedFeed = [Security.SecurityElement]::Escape($feedRoot)
$configText = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="external-kits-local" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
Set-Content -LiteralPath $nugetConfig -Value $configText -Encoding utf8
Write-Host "LOCAL_NUGET_FEED=$feedRoot"
Write-Host "NUGET_CONFIG=$nugetConfig"
