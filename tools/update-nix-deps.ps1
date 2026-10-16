#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
& dotnet restore (Join-Path $repoRoot 'SS14.Watchdog/SS14.Watchdog.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
$assets = Get-Content -Raw (Join-Path $repoRoot 'SS14.Watchdog/obj/project.assets.json') | ConvertFrom-Json
$packages = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Name })
foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
    foreach ($download in $framework.Value.downloadDependencies) {
        $version = ($download.version.Trim('[', ']') -split ',')[0].Trim()
        $packages += $download.name + '/' + $version
    }
}
$dependencies = foreach ($package in $packages | Sort-Object -Unique) {
    $parts = $package.Split('/')
    $packageFile = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder ($parts[0].ToLowerInvariant() + '/' + $parts[1] + '/' + $parts[0].ToLowerInvariant() + '.' + $parts[1] + '.nupkg')
        if (Test-Path -LiteralPath $candidate) { $packageFile = $candidate; break }
    }
    if (-not $packageFile) { throw "Missing package archive: $package" }
    $hash = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($packageFile)
    try { $digest = [Convert]::ToBase64String($hash.ComputeHash($stream)) }
    finally { $stream.Dispose(); $hash.Dispose() }
    [ordered]@{ pname = $parts[0]; version = $parts[1]; hash = 'sha256-' + $digest }
}
$json = ConvertTo-Json -InputObject @($dependencies) -Depth 4
[System.IO.File]::WriteAllText((Join-Path $repoRoot 'nix/deps.json'), $json + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Output "Updated $(@($dependencies).Count) pinned NuGet archives."
