#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GamePath,
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'artifacts/releases')
)
$ErrorActionPreference = 'Stop'
$version = '0.4.3'
$output = [IO.Path]::GetFullPath($OutputRoot)
$run = Join-Path $output ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$name = "CitiesIIAgentBridge-$version-community"
$package = Join-Path $run $name
New-Item -ItemType Directory -Path $package -Force | Out-Null

# Explicit allowlist: never copy local city records, logs, compiler response files,
# game assemblies, credentials, debug symbols, or arbitrary working-tree files.
$files = @(
    'README.md','INSTALL.md','AGENTS.md','DEVELOPMENT.md','SHARING.md',
    'RELEASE-NOTES.md','VALIDATION.txt','CONTRIBUTING.md','COMMANDS.md','commands.json',
    'build.ps1','package.ps1','install.ps1','verify-package.ps1','verify-api.ps1',
    'bridge.ps1','advance.ps1','journal.ps1','export-map.ps1','view-map.html',
    'src/BridgeTick.cs','src/BuildCommands.cs','src/CityCommands.cs','src/Construction.cs',
    'src/Diagnostics.cs','src/Mailbox.cs','src/Mod.cs','src/Neighborhood.cs',
    'src/NetworkCommands.cs','src/ObjectPlacementSafety.cs','src/PlanGeometry.cs',
    'src/QueryPage.cs','src/ServiceDetails.cs','src/ServiceTools.cs','src/Settings.cs',
    'src/SimulationControl.cs','src/SimulationWindow.cs','src/Spatial.cs','src/Workflow.cs',
    'tests/MailboxTests.csproj','tests/Program.cs','tests/PolicyTests.cs',
    'tests/ObjectPlacementTests.cs','tests/QueryPageTests.cs','tests/RecoveryTests.cs',
    'tests/MailboxClientTests.cs','tests/ClientTests.ps1','tests/JournalTests.ps1',
    'tests/PackageTests.ps1','tests/map-viewer.cjs'
)
foreach ($file in $files) {
    $target = Join-Path $package $file
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $target
}
$build = Join-Path $run 'build'
& (Join-Path $PSScriptRoot 'build.ps1') -GamePath $GamePath -OutputDirectory $build -CommunityRelease
$artifacts = Join-Path $package 'artifacts'
New-Item -ItemType Directory -Path $artifacts | Out-Null
foreach ($file in @('CitiesIIAgentBridge.dll','build-manifest.json')) {
    Copy-Item -LiteralPath (Join-Path $build $file) -Destination $artifacts
}
$manifest = Get-Content -LiteralPath (Join-Path $artifacts 'build-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.modVersion -ne $version) { throw 'Package and build versions differ.' }
$hashes = @(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
[ordered]@{release="$version-community"; files=$hashes} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS.json') -Encoding utf8
& (Join-Path $package 'verify-package.ps1')
$zip = Join-Path $run ($name + '.zip')
Compress-Archive -LiteralPath $package -DestinationPath $zip
((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($zip)) | Set-Content -LiteralPath ($zip + '.sha256') -Encoding utf8
Write-Output "Release ZIP: $zip"
Write-Output "Package directory: $package"
