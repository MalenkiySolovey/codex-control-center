[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$Commit
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$RepositoryUrl = "https://github.com/MalenkiySolovey/agent-state-protocol.git"
$VendorParent = Join-Path $RepoRoot "third_party"
$VendorPath = Join-Path $VendorParent "agent-state-protocol"
$ManifestRelativePath = "protocol-manifest.json"
$Guid = [guid]::NewGuid().ToString("N")
$TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("agent-state-protocol-sync-" + $Guid)
$ClonePath = Join-Path $TempRoot "upstream"
$StagePath = Join-Path $VendorParent (".agent-state-protocol-stage-" + $Guid)
$BackupPath = Join-Path $VendorParent (".agent-state-protocol-backup-" + $Guid)
$HadExistingSnapshot = Test-Path -LiteralPath $VendorPath

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

function Assert-GeneratedDirectoryPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$NamePattern
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $parentPrefix = [System.IO.Path]::GetFullPath($Parent).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $fullPath) -notmatch $NamePattern) {
        throw "Refusing to remove a path that is not a generated protocol sync directory: $fullPath"
    }
}

function Test-Snapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedCommit
    )

    $manifestPath = Join-Path $Path $ManifestRelativePath
    $provenancePath = Join-Path $Path "PROVENANCE.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) {
        throw "Staged protocol snapshot is missing its manifest or provenance."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    if ($provenance.repository -ne "https://github.com/MalenkiySolovey/agent-state-protocol" -or $provenance.commit -ne $ExpectedCommit -or $provenance.schemaVersion -ne 1) {
        throw "Staged protocol provenance does not match the requested upstream commit."
    }
    if ((Get-CanonicalSha256 -Path $manifestPath) -ne $provenance.manifestSha256) {
        throw "Staged protocol manifest hash does not match its provenance record."
    }

    foreach ($entry in @($manifest.schemas)) {
        $schemaPath = Join-Path $Path ([string]$entry.path.Replace("/", [System.IO.Path]::DirectorySeparatorChar))
        $safePrefix = [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $fullSchemaPath = [System.IO.Path]::GetFullPath($schemaPath)
        if (-not $fullSchemaPath.StartsWith($safePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Manifest schema path escapes the staged snapshot: $($entry.path)"
        }
        if (-not (Test-Path -LiteralPath $fullSchemaPath -PathType Leaf) -or (Get-CanonicalSha256 -Path $fullSchemaPath) -ne $entry.sha256) {
            throw "Staged canonical schema hash check failed: $($entry.path)"
        }
    }
}

function Test-CanonicalCheckout {
    param([Parameter(Mandatory = $true)][string]$Path)

    $manifestPath = Join-Path $Path $ManifestRelativePath
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.protocol -ne "agent-state-protocol" -or $manifest.schemaVersion -ne 1 -or $manifest.dialect -ne "https://json-schema.org/draft/2020-12/schema") {
        throw "Fetched commit has an unexpected Agent State Protocol manifest."
    }

    $seenIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $seenPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest.schemas)) {
        if ($entry.id -notlike "urn:agent-state-protocol:v1:*" -or $entry.id -match '(?i)codex-control-center') {
            throw "Fetched protocol contains a product-coupled schema ID: $($entry.id)"
        }
        if (-not $seenIds.Add([string]$entry.id) -or -not $seenPaths.Add([string]$entry.path)) {
            throw "Fetched protocol manifest contains a duplicate schema ID or path."
        }
        if ([System.IO.Path]::IsPathRooted([string]$entry.path) -or $entry.path -match '(^|[/\\])\.\.([/\\]|$)') {
            throw "Fetched protocol manifest contains an unsafe schema path: $($entry.path)"
        }

        $schemaPath = Join-Path $Path ([string]$entry.path.Replace("/", [System.IO.Path]::DirectorySeparatorChar))
        $safePrefix = [System.IO.Path]::GetFullPath($Path).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $fullSchemaPath = [System.IO.Path]::GetFullPath($schemaPath)
        if (-not $fullSchemaPath.StartsWith($safePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $fullSchemaPath -PathType Leaf)) {
            throw "Fetched protocol schema path is missing or escapes the repository: $($entry.path)"
        }
        if ((Get-CanonicalSha256 -Path $fullSchemaPath) -ne $entry.sha256) {
            throw "Fetched protocol schema hash does not match its manifest: $($entry.path)"
        }
        $schema = Get-Content -LiteralPath $fullSchemaPath -Raw | ConvertFrom-Json
        if ($schema.'$schema' -ne $manifest.dialect -or $schema.'$id' -ne $entry.id -or $schema.additionalProperties -ne $false -or $schema.properties.schemaVersion.const -ne 1 -or $schema.required -notcontains "schemaVersion") {
            throw "Fetched protocol schema violates its manifest or v1 record invariants: $($entry.path)"
        }

        foreach ($example in @($entry.examples)) {
            if ([System.IO.Path]::IsPathRooted([string]$example) -or $example -match '(^|[/\\])\.\.([/\\]|$)') {
                throw "Fetched protocol manifest contains an unsafe example path: $example"
            }
            $examplePath = Join-Path $Path ([string]$example.Replace("/", [System.IO.Path]::DirectorySeparatorChar))
            $fullExamplePath = [System.IO.Path]::GetFullPath($examplePath)
            if (-not $fullExamplePath.StartsWith($safePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $fullExamplePath -PathType Leaf)) {
                throw "Fetched protocol example is missing or escapes the repository: $example"
            }
        }
    }
    if ($seenIds.Count -ne 9) {
        throw "Fetched protocol must contain exactly nine canonical v1 schemas."
    }
}

if (-not (Test-Path -LiteralPath $VendorParent -PathType Container)) {
    New-Item -ItemType Directory -Path $VendorParent | Out-Null
}
if ($HadExistingSnapshot) {
    $dirtySnapshot = @(& git -C $RepoRoot status --porcelain -- third_party/agent-state-protocol)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect the existing vendored snapshot before replacement."
    }
    if ($dirtySnapshot.Count -gt 0) {
        throw "The current vendored snapshot has local changes; commit or preserve them before syncing."
    }
}

try {
    New-Item -ItemType Directory -Path $TempRoot | Out-Null
    & git clone --no-checkout --no-tags $RepositoryUrl $ClonePath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not clone the canonical Agent State Protocol repository."
    }
    $actualRemote = (& git -C $ClonePath remote get-url origin).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualRemote -ne $RepositoryUrl) {
        throw "The fetched repository URL did not match the canonical protocol repository."
    }

    & git -C $ClonePath fetch --no-tags origin $Commit
    if ($LASTEXITCODE -ne 0) {
        throw "Could not fetch the explicitly requested protocol commit $Commit."
    }
    & git -C $ClonePath checkout --detach $Commit
    if ($LASTEXITCODE -ne 0) {
        throw "Could not check out the explicitly requested protocol commit $Commit."
    }
    $checkedOutCommit = (& git -C $ClonePath rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $checkedOutCommit -ne $Commit) {
        throw "Fetched protocol commit does not match the requested SHA."
    }

    Test-CanonicalCheckout -Path $ClonePath

    New-Item -ItemType Directory -Path (Join-Path $StagePath "schemas\v1") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $StagePath "examples\v1") -Force | Out-Null
    Copy-Item -Path (Join-Path $ClonePath "schemas\v1\*") -Destination (Join-Path $StagePath "schemas\v1") -Recurse -Force
    Copy-Item -Path (Join-Path $ClonePath "examples\v1\*") -Destination (Join-Path $StagePath "examples\v1") -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $ClonePath $ManifestRelativePath) -Destination (Join-Path $StagePath $ManifestRelativePath)

    $manifestHash = Get-CanonicalSha256 -Path (Join-Path $StagePath $ManifestRelativePath)
    $provenance = [ordered]@{
        repository = "https://github.com/MalenkiySolovey/agent-state-protocol"
        commit = $Commit
        schemaVersion = 1
        manifestSha256 = $manifestHash
    }
    $provenanceJson = ($provenance | ConvertTo-Json -Depth 4) + "`n"
    [System.IO.File]::WriteAllText(
        (Join-Path $StagePath "PROVENANCE.json"),
        $provenanceJson,
        [System.Text.UTF8Encoding]::new($false)
    )
    Test-Snapshot -Path $StagePath -ExpectedCommit $Commit

    if ($HadExistingSnapshot) {
        Move-Item -LiteralPath $VendorPath -Destination $BackupPath
    }
    try {
        Move-Item -LiteralPath $StagePath -Destination $VendorPath
        Test-Snapshot -Path $VendorPath -ExpectedCommit $Commit
    }
    catch {
        if (Test-Path -LiteralPath $VendorPath) {
            Move-Item -LiteralPath $VendorPath -Destination $StagePath
        }
        if ($HadExistingSnapshot -and (Test-Path -LiteralPath $BackupPath)) {
            Move-Item -LiteralPath $BackupPath -Destination $VendorPath
        }
        throw
    }

    if ($HadExistingSnapshot -and (Test-Path -LiteralPath $BackupPath)) {
        Assert-GeneratedDirectoryPath -Path $BackupPath -Parent $VendorParent -NamePattern '^\.agent-state-protocol-backup-[0-9a-f]{32}$'
        Remove-Item -LiteralPath $BackupPath -Recurse -Force
    }
    Write-Host "Vendored Agent State Protocol snapshot updated to $Commit." -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $StagePath) {
        Assert-GeneratedDirectoryPath -Path $StagePath -Parent $VendorParent -NamePattern '^\.agent-state-protocol-stage-[0-9a-f]{32}$'
        Remove-Item -LiteralPath $StagePath -Recurse -Force
    }
    if (Test-Path -LiteralPath $TempRoot) {
        Assert-GeneratedDirectoryPath -Path $TempRoot -Parent ([System.IO.Path]::GetTempPath()) -NamePattern '^agent-state-protocol-sync-[0-9a-f]{32}$'
        Remove-Item -LiteralPath $TempRoot -Recurse -Force
    }
}
