#requires -Version 7.0
param([Parameter(Mandatory)][string]$PackageZip, [Parameter(Mandatory)][string]$GamePath)
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('CitiesIIAgentBridge-package-tests-' + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $PackageZip -DestinationPath $testRoot
$package = (Get-ChildItem -LiteralPath $testRoot -Directory | Select-Object -First 1).FullName
$destination = Join-Path $testRoot 'isolated-install'
# This harness only installs into its unique temporary directory, never the game.
function Get-Process { param($Name,$ErrorAction) return $null }
& "$package/install.ps1" -GamePath $GamePath -Destination $destination -CheckOnly
if (Test-Path -LiteralPath $destination) { throw 'CheckOnly wrote destination' }
& "$package/install.ps1" -GamePath $GamePath -Destination $destination -WhatIf
if (Test-Path -LiteralPath $destination) { throw 'WhatIf wrote destination' }
& "$package/install.ps1" -GamePath $GamePath -Destination $destination
$hash = (Get-FileHash -LiteralPath "$destination/CitiesIIAgentBridge.dll").Hash
& "$package/install.ps1" -GamePath $GamePath -Destination $destination
$backups = @(Get-ChildItem -LiteralPath "$destination/backups" -Recurse -Filter '*.dll')
if ($backups.Count -ne 1 -or (Get-FileHash -LiteralPath $backups[0].FullName).Hash -ne $hash) { throw 'Backup not preserved' }
Write-Output 'PASS: CheckOnly/WhatIf are read-only; isolated install and backup match.'
function Get-Process { param($Name,$ErrorAction) return [pscustomobject]@{Name='Cities2'} }
$blocked = $false
try { & "$package/install.ps1" -GamePath $GamePath -Destination "$testRoot/blocked" } catch { if ($_ -notmatch 'is running') { throw }; $blocked=$true }
if (!$blocked -or (Test-Path -LiteralPath "$testRoot/blocked")) { throw 'Running-game guard failed' }
Write-Output 'PASS: simulated running game blocks installation.'
$fake = Join-Path $testRoot 'incompatible/Cities2_Data/Managed'
New-Item -ItemType Directory -Path $fake -Force | Out-Null
Set-Content -LiteralPath "$fake/Game.dll" -Value 'synthetic incompatible assembly'
$blocked = $false
try { & "$package/install.ps1" -GamePath "$testRoot/incompatible" -CheckOnly } catch { if ($_ -notmatch 'fingerprint differs') { throw }; $blocked=$true }
if (!$blocked) { throw 'Incompatible game accepted' }
Write-Output 'PASS: incompatible game fingerprint rejected.'
Add-Content -LiteralPath "$package/bridge.ps1" -Value '# synthetic tampering'
$blocked = $false
try { & "$package/verify-package.ps1" } catch { if ($_ -notmatch 'hash mismatch') { throw }; $blocked=$true }
if (!$blocked) { throw 'Tampering accepted' }
Write-Output 'PASS: package tampering rejected.'
Write-Output "All installation writes stayed in $testRoot"
