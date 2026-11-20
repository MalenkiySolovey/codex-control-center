[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$CatalogCheckout
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$pinPath = Join-Path $repoRoot 'eng\catalog\provider-catalog-pin.json'
$pin = Get-Content -LiteralPath $pinPath -Raw | ConvertFrom-Json
$expectedRepository = 'https://github.com/MalenkiySolovey/llm-provider-catalog'
if ($pin.repository -cne $expectedRepository -or
    [string]$pin.commit -notmatch '^[0-9a-f]{40}$' -or
    [int]$pin.schemaVersion -ne 1 -or
    [int]$pin.catalogVersion -lt 1) {
    throw 'Provider catalog pin manifest has an invalid repository, commit, or version.'
}
foreach ($entry in @(
    @{ Name = 'catalogFile'; Value = 'catalog/providers.catalog.json' },
    @{ Name = 'schemaFile'; Value = 'catalog/providers.catalog.schema.json' },
    @{ Name = 'signatureFile'; Value = 'catalog/providers.catalog.sig' }
)) {
    if ([string]$pin.($entry.Name) -cne $entry.Value) {
        throw "Provider catalog manifest contains an unexpected $($entry.Name) path."
    }
}
foreach ($hashName in @('catalogSha256', 'schemaSha256', 'signatureSha256', 'officialPublicKeySha256')) {
    if ([string]$pin.$hashName -notmatch '^[0-9a-f]{64}$') {
        throw "Provider catalog pin manifest has an invalid $hashName."
    }
}

function Invoke-GitText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    $output = & git -C $Path @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed in '$Path': $($output -join "`n")"
    }
    return ($output -join "`n").Trim()
}

function Test-TreeHasExpectedIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath (Join-Path $Path '.git'))) { return $false }
    try {
        $remote = Invoke-GitText -Path $Path -Arguments @('config', '--get', 'remote.origin.url')
        $normalizedRemote = $remote.TrimEnd('/') -replace '\.git$', ''
        $head = Invoke-GitText -Path $Path -Arguments @('rev-parse', 'HEAD')
        $status = Invoke-GitText -Path $Path -Arguments @('status', '--porcelain', '--untracked-files=normal')
        return $normalizedRemote -ceq $expectedRepository -and $head -ceq [string]$pin.commit -and [string]::IsNullOrWhiteSpace($status)
    }
    catch {
        return $false
    }
}

$temporaryRoot = $null
$temporaryEnvironmentRoot = $null
$sourceRoot = $null
$explicitCheckout = -not [string]::IsNullOrWhiteSpace($CatalogCheckout)
try {
    if ($explicitCheckout) {
        $sourceRoot = (Resolve-Path -LiteralPath $CatalogCheckout).Path
        if (-not (Test-TreeHasExpectedIdentity -Path $sourceRoot)) {
            throw 'Explicit catalog checkout must be clean, use the pinned public repository remote, and be at the pinned commit.'
        }
    }
    else {
        $siblingCheckout = Join-Path (Split-Path -Parent $repoRoot) 'llm-provider-catalog'
        if (Test-Path -LiteralPath $siblingCheckout -PathType Container) {
            $resolvedSibling = (Resolve-Path -LiteralPath $siblingCheckout).Path
            if (Test-TreeHasExpectedIdentity -Path $resolvedSibling) {
                $sourceRoot = $resolvedSibling
            }
        }

        if ($null -eq $sourceRoot) {
            $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('cc-provider-catalog-' + [Guid]::NewGuid().ToString('N'))
            $sourceRoot = Join-Path $temporaryRoot 'checkout'
            [System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
            & git clone --quiet --no-checkout $expectedRepository $sourceRoot
            if ($LASTEXITCODE -ne 0) { throw 'Could not obtain the pinned public provider catalog repository.' }
            & git -C $sourceRoot checkout --quiet --detach ([string]$pin.commit)
            if ($LASTEXITCODE -ne 0) { throw 'Could not check out the pinned provider catalog commit.' }
            if (-not (Test-TreeHasExpectedIdentity -Path $sourceRoot)) {
                throw 'Downloaded provider catalog checkout does not match the repository and commit pin.'
            }
        }
    }

    $treeEntries = & git -C $sourceRoot -c core.quotePath=false ls-tree -r ([string]$pin.commit) 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the pinned provider catalog tree.' }
    $treePaths = @{}
    foreach ($entry in $treeEntries) {
        if ($entry -notmatch '^([0-9]{6}) (blob|commit) [0-9a-f]+\t(.+)$') {
            throw "Unexpected entry in pinned provider catalog tree: $entry"
        }
        $mode = $Matches[1]
        $objectType = $Matches[2]
        $relativePath = $Matches[3]
        if ($mode -eq '120000' -or $objectType -eq 'commit') {
            throw "Pinned provider catalog tree contains a symlink or submodule: $relativePath"
        }
        if ([System.IO.Path]::IsPathRooted($relativePath) -or $relativePath -match '(^|[/\\])\.\.([/\\]|$)') {
            throw "Pinned provider catalog tree contains an unsafe path: $relativePath"
        }
        $treePaths[$relativePath] = $mode
    }
    foreach ($relativePath in @($pin.catalogFile, $pin.schemaFile, $pin.signatureFile, 'trust/official-catalog-public-key.spki.b64')) {
        if (-not $treePaths.ContainsKey([string]$relativePath) -or $treePaths[[string]$relativePath] -ne '100644') {
            throw "Pinned provider catalog file is missing or not a regular file: $relativePath"
        }
    }

    $catalogSource = Join-Path $sourceRoot ([string]$pin.catalogFile)
    $schemaSource = Join-Path $sourceRoot ([string]$pin.schemaFile)
    $signatureSource = Join-Path $sourceRoot ([string]$pin.signatureFile)
    $keySource = Join-Path $sourceRoot 'trust\official-catalog-public-key.spki.b64'
    foreach ($sourceFile in @($catalogSource, $schemaSource, $signatureSource, $keySource)) {
        $sourceItem = Get-Item -LiteralPath $sourceFile -Force
        if (-not $sourceItem.PSIsContainer -and ($sourceItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Provider catalog source file is a reparse point: $sourceFile"
        }
        if ($sourceItem.PSIsContainer) { throw "Provider catalog source file is not a regular file: $sourceFile" }
    }

    $catalogHash = (Get-FileHash -LiteralPath $catalogSource -Algorithm SHA256).Hash.ToLowerInvariant()
    $schemaHash = (Get-FileHash -LiteralPath $schemaSource -Algorithm SHA256).Hash.ToLowerInvariant()
    $signatureHash = (Get-FileHash -LiteralPath $signatureSource -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($catalogHash -cne [string]$pin.catalogSha256 -or
        $schemaHash -cne [string]$pin.schemaSha256 -or
        $signatureHash -cne [string]$pin.signatureSha256) {
        throw 'Provider catalog source file hashes do not match the immutable pin manifest.'
    }

    $providerKey = (Get-Content -LiteralPath $keySource -Raw -Encoding ASCII).Trim()
    $securityConstants = Get-Content -LiteralPath (Join-Path $repoRoot 'src\CodexSwitcher.Core\Providers\Catalog\CatalogSecurityConstants.cs') -Raw
    $keyMatch = [regex]::Match($securityConstants, 'OfficialCatalogPublicKeyBase64\s*=\s*"(?<key>[A-Za-z0-9+/=]+)"')
    if (-not $keyMatch.Success -or $providerKey -cne $keyMatch.Groups['key'].Value) {
        throw 'Catalog public key does not match the existing embedded official verification key.'
    }
    $publicKeyBytes = [Convert]::FromBase64String($providerKey)
    $keyHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($publicKeyBytes)).ToLowerInvariant()
    if ($keyHash -cne [string]$pin.officialPublicKeySha256) {
        throw 'Official provider catalog public key fingerprint differs from the pin manifest.'
    }

    $catalogBytes = [System.IO.File]::ReadAllBytes($catalogSource)
    $signatureText = [System.IO.File]::ReadAllText($signatureSource, [System.Text.Encoding]::ASCII).Trim()
    $signatureBytes = [Convert]::FromBase64String($signatureText)
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $bytesRead = 0
        $ecdsa.ImportSubjectPublicKeyInfo($publicKeyBytes, [ref]$bytesRead)
        $signatureValid = $false
        if ($signatureBytes.Length -eq 64) {
            $signatureValid = $ecdsa.VerifyData(
                $catalogBytes,
                $signatureBytes,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation
            )
        }
        if (-not $signatureValid) {
            $signatureValid = $ecdsa.VerifyData(
                $catalogBytes,
                $signatureBytes,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence
            )
        }
        if (-not $signatureValid) { throw 'Provider catalog detached signature failed against the existing official key.' }
    }
    finally {
        $ecdsa.Dispose()
    }

    $catalogDocument = [System.Text.Json.JsonDocument]::Parse([System.Text.Encoding]::UTF8.GetString($catalogBytes))
    try {
        $rootElement = $catalogDocument.RootElement
        if ($rootElement.GetProperty('schemaVersion').GetInt32() -ne [int]$pin.schemaVersion -or
            $rootElement.GetProperty('catalogVersion').GetInt32() -ne [int]$pin.catalogVersion) {
            throw 'Provider catalog document versions do not match the pin manifest.'
        }
    }
    finally {
        $catalogDocument.Dispose()
    }

    $pythonCommand = Get-Command py -ErrorAction SilentlyContinue
    $pythonPrefix = @()
    if ($null -ne $pythonCommand) {
        $pythonExecutable = $pythonCommand.Source
        $pythonPrefix = @('-3')
    }
    else {
        $pythonCommand = Get-Command python -ErrorAction SilentlyContinue
        if ($null -eq $pythonCommand) { throw 'Python 3.12 or newer is required to validate the pinned catalog.' }
        $pythonExecutable = $pythonCommand.Source
    }
    $pythonProbe = & $pythonExecutable @pythonPrefix -c 'import sys; print(sys.executable); print(sys.version_info >= (3, 12))' 2>&1
    if ($LASTEXITCODE -ne 0 -or $pythonProbe.Count -lt 2 -or $pythonProbe[-1] -ne 'True') {
        throw "Python 3.12 or newer is required to validate the pinned catalog. $($pythonProbe -join ' ')"
    }
    $temporaryEnvironmentRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('cc-provider-catalog-tools-' + [Guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($temporaryEnvironmentRoot) | Out-Null
    $pythonVenv = Join-Path $temporaryEnvironmentRoot 'venv'
    $pythonVenvExecutable = Join-Path $pythonVenv 'Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $pythonVenvExecutable -PathType Leaf)) {
        & $pythonExecutable @pythonPrefix -m venv $pythonVenv
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated provider catalog validation environment.' }
    }
    $requirements = Join-Path $sourceRoot 'tools\requirements.txt'
    & $pythonVenvExecutable -m pip install --disable-pip-version-check -r $requirements
    if ($LASTEXITCODE -ne 0) { throw 'Could not install pinned provider catalog validation dependencies.' }
    & $pythonVenvExecutable (Join-Path $sourceRoot 'tools\verify_catalog.py')
    if ($LASTEXITCODE -ne 0) { throw 'Provider catalog schema, semantic, or signature validation failed.' }
    & $pythonVenvExecutable -m unittest discover -s (Join-Path $sourceRoot 'tests') -v
    if ($LASTEXITCODE -ne 0) { throw 'Provider catalog negative and contract tests failed.' }

    $artifactDirectory = Join-Path $repoRoot 'artifacts\provider-catalog'
    $artifactsRoot = Join-Path $repoRoot 'artifacts'
    if (Test-Path -LiteralPath $artifactsRoot) {
        $artifactsItem = Get-Item -LiteralPath $artifactsRoot -Force
        if ($artifactsItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw 'Refusing to materialize catalog through an artifacts reparse point.'
        }
    }
    [System.IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null
    $artifactRootResolved = [System.IO.Path]::GetFullPath($artifactDirectory)
    $repoRootPrefix = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $artifactRootResolved.StartsWith($repoRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Generated provider catalog path resolves outside the Control Center checkout.'
    }
    $artifactDirectoryItem = Get-Item -LiteralPath $artifactDirectory -Force
    if ($artifactDirectoryItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw 'Refusing to materialize catalog through a generated directory reparse point.'
    }

    foreach ($file in @(
        @{ Source = $catalogSource; Name = 'providers.catalog.json'; Hash = [string]$pin.catalogSha256 },
        @{ Source = $schemaSource; Name = 'providers.catalog.schema.json'; Hash = [string]$pin.schemaSha256 },
        @{ Source = $signatureSource; Name = 'providers.catalog.sig'; Hash = [string]$pin.signatureSha256 }
    )) {
        $destination = Join-Path $artifactDirectory $file.Name
        [System.IO.File]::WriteAllBytes($destination, [System.IO.File]::ReadAllBytes($file.Source))
        $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($destinationHash -cne $file.Hash) { throw "Materialized provider catalog file failed hash verification: $($file.Name)" }
    }

    Write-Host "PROVIDER_CATALOG_PIN=$($pin.commit)"
    Write-Host "PROVIDER_CATALOG_SOURCE=$expectedRepository"
    Write-Host "PROVIDER_CATALOG_MATERIALIZED=$artifactDirectory"
}
finally {
    if ($null -ne $temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
        $resolvedTemp = [System.IO.Path]::GetFullPath($temporaryRoot)
        $systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedTemp.StartsWith($systemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove a provider catalog checkout outside the system temporary directory.'
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
    if ($null -ne $temporaryEnvironmentRoot -and (Test-Path -LiteralPath $temporaryEnvironmentRoot)) {
        $resolvedEnvironmentTemp = [System.IO.Path]::GetFullPath($temporaryEnvironmentRoot)
        $systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedEnvironmentTemp.StartsWith($systemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove a provider catalog validation environment outside the system temporary directory.'
        }
        Remove-Item -LiteralPath $resolvedEnvironmentTemp -Recurse -Force
    }
}
