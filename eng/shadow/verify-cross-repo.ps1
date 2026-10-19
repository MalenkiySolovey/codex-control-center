[CmdletBinding()]
param(
    [string]$DaemonCheckout
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$pinPath = Join-Path $PSScriptRoot 'agent-state-daemon-pin.json'
$pin = Get-Content $pinPath -Raw | ConvertFrom-Json
if ($pin.commit -notmatch '^[0-9a-f]{40}$' -or
    $pin.protocolManifestSha256 -notmatch '^[0-9a-f]{64}$' -or
    $pin.protocolSchemaVersion -ne 1) {
    throw 'The pinned daemon compatibility baseline is invalid.'
}

$createdTemporaryCheckout = $false
$temporaryRoot = $null
if ([string]::IsNullOrWhiteSpace($DaemonCheckout)) {
    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('cc-shadow-ci-' + [Guid]::NewGuid().ToString('N'))
    $DaemonCheckout = Join-Path $temporaryRoot 'agent-state-daemon'
    [System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $createdTemporaryCheckout = $true
    & git clone --quiet --no-checkout $pin.repository $DaemonCheckout *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not obtain the pinned daemon source.'
    }
    & git -C $DaemonCheckout checkout --quiet --detach $pin.commit *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not check out the pinned daemon revision.'
    }
}

try {
    $daemonRoot = (Resolve-Path $DaemonCheckout).Path
    $actualCommit = (& git -C $daemonRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -cne [string]$pin.commit) {
        throw 'The daemon checkout does not match the pinned revision.'
    }

    $daemonSolution = Join-Path $daemonRoot 'AgentStateDaemon.sln'
    & dotnet restore $daemonSolution --locked-mode -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Pinned daemon locked restore failed.' }
    & dotnet build $daemonSolution --configuration Release --no-restore -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Pinned daemon Release build failed.' }

    $daemonExecutable = Join-Path $daemonRoot 'src\AgentStateDaemon.Host\bin\Release\net10.0\AgentStateDaemon.Host.exe'
    if (-not (Test-Path -LiteralPath $daemonExecutable -PathType Leaf)) {
        throw 'The pinned daemon Release executable was not produced.'
    }

    $crossRepoProject = Join-Path $repoRoot 'tests\CodexSwitcher.AgentStateShadow.CrossRepoTests\CodexSwitcher.AgentStateShadow.CrossRepoTests.csproj'
    & dotnet restore $crossRepoProject --locked-mode -p:Configuration=Release -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Cross-repo test locked restore failed.' }

    $previousOptIn = $env:CODEX_CONTROL_CENTER_AGENT_STATE_SHADOW
    $previousExecutable = $env:CODEX_CONTROL_CENTER_AGENT_STATE_DAEMON
    try {
        $env:CODEX_CONTROL_CENTER_AGENT_STATE_SHADOW = '1'
        $env:CODEX_CONTROL_CENTER_AGENT_STATE_DAEMON = $daemonExecutable
        & dotnet test $crossRepoProject --configuration Release --no-restore -p:NuGetAudit=false --logger 'console;verbosity=minimal'
        if ($LASTEXITCODE -ne 0) { throw 'The real-process cross-repo shadow integration failed.' }
    }
    finally {
        $env:CODEX_CONTROL_CENTER_AGENT_STATE_SHADOW = $previousOptIn
        $env:CODEX_CONTROL_CENTER_AGENT_STATE_DAEMON = $previousExecutable
    }

    Write-Host "CROSS_REPO_DAEMON_COMMIT=$actualCommit"
    Write-Host 'CROSS_REPO_RESULT=PASS'
}
finally {
    if ($createdTemporaryCheckout -and $null -ne $temporaryRoot) {
        $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
        $systemTemporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $resolvedTemporaryRoot.StartsWith($systemTemporaryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove a temporary checkout outside the system temp directory.'
        }
        if (Test-Path -LiteralPath $resolvedTemporaryRoot) {
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
        }
    }
}
