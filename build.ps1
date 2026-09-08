param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [switch]$CommunityRelease)
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GamePath 'Cities2_Data\Managed'
if (!(Test-Path -LiteralPath (Join-Path $managed 'Game.dll'))) { throw 'Game.dll not found' }
$sdkLine = (& dotnet --list-sdks | Select-Object -Last 1)
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A .NET SDK is required' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$out = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $out | Out-Null
# Compile against the game's own framework, matching the official template's net48 target.
# Construction tools inherit the game's generated tool systems; no new components, SystemAPI, or Burst jobs.
$references = @('mscorlib','System','System.Core','System.Runtime','netstandard','Game',
    'Colossal.Core','Colossal.Collections','Colossal.Logging','Colossal.Localization','Colossal.IO','Colossal.IO.AssetDatabase',
    'Colossal.UI','Colossal.UI.Binding','Colossal.Mathematics','UnityEngine.CoreModule',
    'Unity.Entities','Unity.Collections','Unity.Mathematics','Unity.Burst','Unity.InputSystem','Newtonsoft.Json')
$debugOption = if ($CommunityRelease) { '/debug-' } else { '/debug:portable' }
$response = @('/nologo','/target:library','/langversion:9.0','/nostdlib+','/optimize+', $debugOption,
    ('/out:"' + (Join-Path $out 'CitiesIIAgentBridge.dll') + '"'))
foreach ($reference in $references) {
    $path = Join-Path $managed ($reference + '.dll')
    if (!(Test-Path -LiteralPath $path)) { throw "Missing reference: $path" }
    $response += '/reference:"' + $path + '"'
}
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $response += '"' + $_.FullName + '"' }
$rsp = Join-Path $out 'compile.rsp'
[IO.File]::WriteAllLines($rsp, $response)
& dotnet $compiler ('@' + $rsp)
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
$manifest = [ordered]@{
    modVersion = '0.4.2'
    builtUtc = [DateTime]::UtcNow.ToString('O')
    gameAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $managed 'Game.dll')).Hash
    dllSha256 = (Get-FileHash -LiteralPath (Join-Path $out 'CitiesIIAgentBridge.dll')).Hash
    compilation = 'Managed IMod with derived native tools; no new components, SystemAPI, or Burst jobs'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'build-manifest.json') -Encoding utf8
Write-Output "Built: $(Join-Path $out 'CitiesIIAgentBridge.dll')"
