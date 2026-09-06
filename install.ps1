#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
 [Parameter(Mandatory)][string]$GamePath,
 [string]$Destination=(Join-Path $env:USERPROFILE 'AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CitiesIIAgentBridge'),
 [switch]$CheckOnly
)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'verify-package.ps1')
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'artifacts\build-manifest.json') -Raw|ConvertFrom-Json
$assembly=Join-Path $GamePath 'Cities2_Data\Managed\Game.dll'
if(!(Test-Path -LiteralPath $assembly -PathType Leaf)){throw 'Game.dll not found. Supply your actual Cities: Skylines II game directory.'}
if((Get-FileHash -LiteralPath $assembly).Hash -ne $manifest.gameAssemblySha256){throw 'Game assembly fingerprint differs from the tested build. This community release is not verified for your game version.'}
$dll=Join-Path $PSScriptRoot 'artifacts\CitiesIIAgentBridge.dll'
if((Get-FileHash -LiteralPath $dll).Hash -ne $manifest.dllSha256){throw 'DLL does not match build manifest.'}
$resolvedDestination=[IO.Path]::GetFullPath($Destination)
Write-Output "Compatible assembly fingerprint. Destination: $resolvedDestination"
if($CheckOnly){Write-Output 'CheckOnly: nothing installed; no game commands sent.';return}
if(Get-Process -Name Cities2 -ErrorAction SilentlyContinue){throw 'Cities: Skylines II is running. Have the owner save and close it. The installer will not stop it.'}
if(!$PSCmdlet.ShouldProcess($resolvedDestination,'Install CitiesIIAgentBridge.dll')){return}
New-Item -ItemType Directory -Force -Path $resolvedDestination|Out-Null
$target=Join-Path $resolvedDestination 'CitiesIIAgentBridge.dll'
$previous=@('CitiesIIAgentBridge.dll','CitiesIIAgentBridge.pdb')|Where-Object {Test-Path -LiteralPath (Join-Path $resolvedDestination $_)}
if($previous){
 $backup=Join-Path $resolvedDestination ('backups\'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))
 New-Item -ItemType Directory -Force -Path $backup|Out-Null
 foreach($name in $previous){Copy-Item -LiteralPath (Join-Path $resolvedDestination $name) -Destination $backup}
 Write-Output "Previous files backed up: $backup"
}
Copy-Item -LiteralPath $dll -Destination $target -Force
if((Get-FileHash -LiteralPath $target).Hash -ne $manifest.dllSha256){throw 'Installed copy hash mismatch. Keep the backup and inspect before launching.'}
Write-Output 'DLL installed and hash-verified. Launch/load and confirm the mod in-game to verify runtime installation.'
