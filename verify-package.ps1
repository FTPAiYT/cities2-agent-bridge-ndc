#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PSScriptRoot)+[IO.Path]::DirectorySeparatorChar
$manifest=Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.json') -Raw|ConvertFrom-Json
if(!$manifest.files -or @($manifest.files).Count -lt 5){throw 'Invalid package manifest.'}
foreach($file in $manifest.files){
 $path=[IO.Path]::GetFullPath((Join-Path $root $file.path))
 if(!$path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest path escapes package.'}
 if(!(Test-Path -LiteralPath $path -PathType Leaf)){throw "Missing package file: $($file.path)"}
 if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256){throw "Package hash mismatch: $($file.path)"}
}
Write-Output "Verified $(@($manifest.files).Count) package files. Hashes verify consistency, not publisher identity."
