[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ProtocolRoot = Join-Path $RepoRoot "third_party\agent-state-protocol"
$ManifestPath = Join-Path $ProtocolRoot "protocol-manifest.json"
$ProvenancePath = Join-Path $ProtocolRoot "PROVENANCE.json"
$SchemaPrefix = "urn:agent-state-protocol:v1:"
$Dialect = "https://json-schema.org/draft/2020-12/schema"
$CanonicalRepository = "https://github.com/MalenkiySolovey/agent-state-protocol"

function Get-CanonicalSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes).Replace("`r`n", "`n")
    $canonicalBytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha.ComputeHash($canonicalBytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Resolve-SnapshotPath {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Root
    )

    if ([System.IO.Path]::IsPathRooted($RelativePath) -or $RelativePath -match '(^|[/\\])\.\.([/\\]|$)') {
        throw "Snapshot path must be relative and may not traverse directories: $RelativePath"
    }

    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $Root ($RelativePath.Replace("/", [System.IO.Path]::DirectorySeparatorChar))))
    $rootPrefix = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Snapshot path resolves outside the vendored directory: $RelativePath"
    }

    return $fullPath
}

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $ProvenancePath -PathType Leaf)) {
    throw "Pinned Agent State Protocol manifest or provenance is missing."
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$provenance = Get-Content -LiteralPath $ProvenancePath -Raw | ConvertFrom-Json
if ($manifest.protocol -ne "agent-state-protocol" -or $manifest.schemaVersion -ne 1 -or $manifest.dialect -ne $Dialect) {
    throw "Vendored protocol manifest has an unexpected identity, version, or dialect."
}
if ($provenance.repository -ne $CanonicalRepository -or $provenance.schemaVersion -ne 1 -or $provenance.commit -notmatch '^[0-9a-f]{40}$') {
    throw "Vendored protocol provenance is not pinned to the expected repository and commit format."
}
if ((Get-CanonicalSha256 -Path $ManifestPath) -ne $provenance.manifestSha256) {
    throw "Vendored protocol manifest hash does not match PROVENANCE.json."
}

$seenIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$seenPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in @($manifest.schemas)) {
    if ($entry.id -notlike "$SchemaPrefix*" -or $entry.id -match '(?i)codex-control-center') {
        throw "Schema ID is not product-neutral: $($entry.id)"
    }
    if (-not $seenIds.Add([string]$entry.id)) {
        throw "Duplicate canonical schema ID: $($entry.id)"
    }
    if (-not $seenPaths.Add([string]$entry.path)) {
        throw "Duplicate schema path in manifest: $($entry.path)"
    }

    $schemaPath = Resolve-SnapshotPath -RelativePath ([string]$entry.path) -Root $ProtocolRoot
    if (-not (Test-Path -LiteralPath $schemaPath -PathType Leaf)) {
        throw "Vendored schema is missing: $($entry.path)"
    }
    if ((Get-CanonicalSha256 -Path $schemaPath) -ne $entry.sha256) {
        throw "Vendored schema hash does not match the protocol manifest: $($entry.path)"
    }

    $schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
    if ($schema.'$schema' -ne $Dialect -or $schema.'$id' -ne $entry.id) {
        throw "Vendored schema identity or dialect disagrees with the manifest: $($entry.path)"
    }
    if ($schema.additionalProperties -ne $false -or $schema.properties.schemaVersion.const -ne 1 -or $schema.required -notcontains "schemaVersion") {
        throw "Vendored top-level record strictness or schemaVersion invariant failed: $($entry.path)"
    }

    foreach ($example in @($entry.examples)) {
        $examplePath = Resolve-SnapshotPath -RelativePath ([string]$example) -Root $ProtocolRoot
        if (-not (Test-Path -LiteralPath $examplePath -PathType Leaf)) {
            throw "Vendored protocol example is missing: $example"
        }
    }
}

$schemaDirectory = Join-Path $ProtocolRoot "schemas\v1"
$actualSchemas = @(Get-ChildItem -LiteralPath $schemaDirectory -Filter "*.schema.json" -File -Recurse)
if ($actualSchemas.Count -ne $seenIds.Count -or $seenIds.Count -ne 9) {
    throw "Vendored schema inventory does not match the nine canonical v1 records."
}

$legacyPath = Join-Path $RepoRoot "contracts\agent-state\v1"
if (Test-Path -LiteralPath $legacyPath) {
    $legacyFiles = @(Get-ChildItem -LiteralPath $legacyPath -File -Recurse -ErrorAction SilentlyContinue)
    if ($legacyFiles.Count -gt 0) {
        throw "Obsolete contracts/agent-state/v1 still contains files and may look canonical."
    }
}

Write-Host "Pinned Agent State Protocol manifest, provenance, nine schema hashes, IDs, and examples verified offline." -ForegroundColor Green
Write-Host "Running the focused DTO-to-schema conformance tests without restore or network access..." -ForegroundColor Cyan
$TestProject = Join-Path $RepoRoot "tests\CodexSwitcher.Core.Tests\CodexSwitcher.Core.Tests.csproj"
& dotnet test $TestProject -c Release --no-build --no-restore --filter "FullyQualifiedName~AgentStateContractTests"
if ($LASTEXITCODE -ne 0) {
    throw "Offline Agent State Protocol DTO conformance tests failed with exit code $LASTEXITCODE."
}

Write-Host "Offline Agent State Protocol verification passed." -ForegroundColor Green
