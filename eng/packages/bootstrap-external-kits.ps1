[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Use-IsolatedNuGetCache.ps1')

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$pinPath = Join-Path $PSScriptRoot 'external-kits.json'
$pins = Get-Content -LiteralPath $pinPath -Raw | ConvertFrom-Json
if ($pins.schemaVersion -ne 1 -or $null -eq $pins.kits -or @($pins.kits).Count -lt 1) {
    throw 'The external kit pin manifest is invalid.'
}

$kitNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$packageIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($kit in $pins.kits) {
    $requiredProperties = @('name', 'repository', 'commit', 'project', 'packageId', 'version')
    if ($null -eq $kit -or @($requiredProperties | Where-Object { $null -eq $kit.PSObject.Properties[$_] }).Count -gt 0) {
        throw 'The external kit pin manifest contains an incomplete kit entry.'
    }

    if ($kit.name -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' -or
        $kit.repository -notmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.git$' -or
        $kit.commit -notmatch '^[0-9a-f]{40}$' -or
        $kit.project -notmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*\.csproj$' -or
        $kit.project -match '(^|/)\.{1,2}(/|$)' -or
        $kit.packageId -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$' -or
        $kit.version -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]*$') {
        throw "Invalid exact pin or package metadata for '$($kit.name)'."
    }

    if (-not $kitNames.Add($kit.name) -or -not $packageIds.Add($kit.packageId)) {
        throw "External kit names and package IDs must be unique; duplicate entry '$($kit.name)'."
    }
}

$artifactsRoot = Join-Path $repoRoot 'artifacts'
$sourceRoot = Join-Path $artifactsRoot 'external-kits\sources'
$feedRoot = Join-Path $artifactsRoot 'local-packages'
$nugetConfig = Join-Path $artifactsRoot 'external-kits\NuGet.Config'
$systemTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
New-Item -ItemType Directory -Path $feedRoot -Force | Out-Null

function Assert-WithinRoot([string]$Path, [string]$Root) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to modify a path outside its validated root.'
    }
}

function ConvertTo-Base64Sha512([string]$Hash) {
    if ($Hash -notmatch '^[0-9A-Fa-f]{128}$') { throw 'A SHA512 hash has an invalid format.' }
    $bytes = [Array]::CreateInstance([byte], $Hash.Length / 2)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [Convert]::ToByte($Hash.Substring($index * 2, 2), 16)
    }
    return [Convert]::ToBase64String($bytes)
}

Assert-WithinRoot $sourceRoot $artifactsRoot
Assert-WithinRoot $feedRoot $artifactsRoot
Get-ChildItem -LiteralPath $feedRoot -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.nupkg', '.snupkg' } |
    Remove-Item -Force

Push-Location $repoRoot
try {
$expectedSdk = (Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$actualSdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $actualSdk -cne $expectedSdk) {
    throw "External packages require the pinned .NET SDK $expectedSdk; found '$actualSdk'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($kit in $pins.kits) {
    $checkout = Join-Path $sourceRoot "$($kit.name)-$($kit.commit)"
    $createdCheckout = $false
    if (-not (Test-Path -LiteralPath $checkout)) {
        & git clone --quiet --no-checkout $kit.repository $checkout
        if ($LASTEXITCODE -ne 0) { throw "Could not clone $($kit.name)." }
        $createdCheckout = $true
    }
    $actualHead = (& git -C $checkout rev-parse HEAD 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualHead -cne $kit.commit) {
        & git -C $checkout fetch --quiet origin $kit.commit
        if ($LASTEXITCODE -ne 0) { throw "Could not fetch pinned commit for $($kit.name)." }
    }

    if (-not $createdCheckout) {
        $sourceStatus = @(& git -C $checkout status --porcelain)
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect the $($kit.name) checkout." }
        if ($sourceStatus.Count -gt 0) {
            throw "The pinned $($kit.name) checkout contains local changes; refusing to overwrite them."
        }
    }

    & git -C $checkout checkout --quiet --detach $kit.commit
    if ($LASTEXITCODE -ne 0) { throw "Could not check out pinned commit for $($kit.name)." }
    $actualHead = (& git -C $checkout rev-parse HEAD).Trim()
    if ($actualHead -cne $kit.commit) { throw "Pinned SHA mismatch for $($kit.name)." }
    $pinnedProject = Join-Path $checkout ($kit.project -replace '/', '\')
    if (-not (Test-Path -LiteralPath $pinnedProject -PathType Leaf)) { throw "Package project is missing for $($kit.name)." }
    Assert-WithinRoot $checkout $sourceRoot
    $verificationRoot = Join-Path $artifactsRoot ("external-kits\package-verification\$($kit.name)-$([Guid]::NewGuid().ToString('N'))")
    Assert-WithinRoot $verificationRoot $artifactsRoot
    $firstOutput = Join-Path $verificationRoot 'first'
    $secondOutput = Join-Path $verificationRoot 'second'
    $packageSourceRoot = Join-Path $systemTempRoot ("codex-kit-build-$($kit.name)-$([Guid]::NewGuid().ToString('N'))")
    Assert-WithinRoot $packageSourceRoot $systemTempRoot
    New-Item -ItemType Directory -Path $firstOutput, $secondOutput -Force | Out-Null
    try {
        $treeEntries = @(& git -C $checkout ls-tree -r $kit.commit)
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect the pinned $($kit.name) source tree." }
        if (@($treeEntries | Where-Object { $_ -match '^(120000|160000)\s' }).Count -gt 0) {
            throw "The pinned $($kit.name) source tree contains a symlink or submodule."
        }
        & git clone --quiet --no-checkout $checkout $packageSourceRoot
        if ($LASTEXITCODE -ne 0) { throw "Could not create a build checkout for $($kit.name)." }
        & git -C $packageSourceRoot config core.autocrlf false
        if ($LASTEXITCODE -ne 0) { throw "Could not normalize the $($kit.name) build checkout." }
        & git -C $packageSourceRoot checkout --quiet --detach $kit.commit
        if ($LASTEXITCODE -ne 0) { throw "Could not materialize the pinned $($kit.name) build checkout." }
        $buildHead = (& git -C $packageSourceRoot rev-parse HEAD).Trim()
        if ($buildHead -cne $kit.commit) { throw "Build checkout SHA mismatch for $($kit.name)." }
        $buildStatus = @(& git -C $packageSourceRoot status --porcelain)
        if ($LASTEXITCODE -ne 0 -or $buildStatus.Count -gt 0) { throw "Build checkout is not clean for $($kit.name)." }
        $crlfFiles = @(& git -C $packageSourceRoot ls-files --eol | Where-Object { $_ -match '\bw/crlf\b' })
        if ($LASTEXITCODE -ne 0 -or $crlfFiles.Count -gt 0) {
            throw "The $($kit.name) build checkout contains noncanonical CRLF files."
        }
        $sourceReparsePoints = @(Get-ChildItem -LiteralPath $packageSourceRoot -Recurse -Force | Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        })
        if ($sourceReparsePoints.Count -gt 0) { throw "The $($kit.name) build checkout contains a reparse point." }

        $project = Join-Path $packageSourceRoot ($kit.project -replace '/', '\')
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Build checkout package project is missing for $($kit.name)." }
        Assert-WithinRoot $project $packageSourceRoot
        foreach ($output in @($firstOutput, $secondOutput)) {
            $packArgs = @(
                $project,
                '--configuration', 'Release',
                '--output', $output,
                "-p:PackageVersion=$($kit.version)",
                '-p:ContinuousIntegrationBuild=true',
                '-p:Deterministic=true',
                '-p:DeterministicTimestamp=2020-01-01T00:00:00Z',
                "-p:RepositoryCommit=$($kit.commit)"
            )
            & dotnet pack @packArgs
            if ($LASTEXITCODE -ne 0) { throw "Package build failed for $($kit.name)." }
        }

        $packageName = "$($kit.packageId).$($kit.version).nupkg"
        $firstPackage = Join-Path $firstOutput $packageName
        $secondPackage = Join-Path $secondOutput $packageName
        foreach ($package in @($firstPackage, $secondPackage)) {
            if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "Expected package is missing for $($kit.name)." }
        }
        $firstHash = (Get-FileHash -LiteralPath $firstPackage -Algorithm SHA256).Hash
        $secondHash = (Get-FileHash -LiteralPath $secondPackage -Algorithm SHA256).Hash
        if ($firstHash -cne $secondHash) {
            throw "Package output is not reproducible for $($kit.name): $firstHash != $secondHash"
        }
        $firstContentHash = ConvertTo-Base64Sha512 (Get-FileHash -LiteralPath $firstPackage -Algorithm SHA512).Hash
        $secondContentHash = ConvertTo-Base64Sha512 (Get-FileHash -LiteralPath $secondPackage -Algorithm SHA512).Hash
        if ($firstContentHash -cne $secondContentHash) {
            throw "NuGet package content hash differs for $($kit.name)."
        }

        $archive = [IO.Compression.ZipFile]::OpenRead($firstPackage)
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

        $packagePath = Join-Path $feedRoot $packageName
        Copy-Item -LiteralPath $firstPackage -Destination $packagePath -Force
        Write-Host "$($kit.packageId)@$($kit.version) pinned=$($kit.commit) sha256=$firstHash contentHash=$firstContentHash verified"
    }
    finally {
        if (Test-Path -LiteralPath $packageSourceRoot) {
            $packageSourceItem = Get-Item -LiteralPath $packageSourceRoot -Force
            if (($packageSourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to clean reparse-point build checkout for $($kit.name)."
            }
            Assert-WithinRoot $packageSourceRoot $systemTempRoot
            $packageSourceReparsePoints = @(Get-ChildItem -LiteralPath $packageSourceRoot -Recurse -Force | Where-Object {
                ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            })
            if ($packageSourceReparsePoints.Count -gt 0) {
                throw "Refusing to clean build checkout containing a reparse point for $($kit.name)."
            }
            Remove-Item -LiteralPath $packageSourceRoot -Recurse -Force
        }
        if (Test-Path -LiteralPath $verificationRoot) {
            $verificationItem = Get-Item -LiteralPath $verificationRoot -Force
            if (($verificationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to clean reparse-point package verification output for $($kit.name)."
            }
            Assert-WithinRoot $verificationRoot $artifactsRoot
            $verificationReparsePoints = @(Get-ChildItem -LiteralPath $verificationRoot -Recurse -Force | Where-Object {
                ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            })
            if ($verificationReparsePoints.Count -gt 0) {
                throw "Refusing to clean package verification output containing a reparse point for $($kit.name)."
            }
            Remove-Item -LiteralPath $verificationRoot -Recurse -Force
        }
    }
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
}
finally {
    Pop-Location
}
