$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$cacheRoot = $env:CODEX_CCC_NUGET_CACHE_ROOT
if ([string]::IsNullOrWhiteSpace($cacheRoot)) {
    $cacheRoot = Join-Path $temporaryRoot ('codex-control-center-nuget-' + [Guid]::NewGuid().ToString('N'))
    $env:CODEX_CCC_NUGET_CACHE_ROOT = $cacheRoot
}

$resolvedCacheRoot = [System.IO.Path]::GetFullPath($cacheRoot)
if (-not $resolvedCacheRoot.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The isolated NuGet package cache must stay inside the system temporary directory.'
}

[System.IO.Directory]::CreateDirectory($resolvedCacheRoot) | Out-Null
$env:NUGET_PACKAGES = $resolvedCacheRoot
Write-Host "ISOLATED_NUGET_PACKAGES=$resolvedCacheRoot"
