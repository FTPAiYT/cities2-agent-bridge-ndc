#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$GamePath,
    [Parameter(Mandatory)][string]$BridgeAssemblyPath
)
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GamePath 'Cities2_Data/Managed'
Add-Type -Path (Join-Path $managed 'Colossal.Mono.Cecil.dll')
$game = [Colossal.Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'Game.dll'))
$bridge = [Colossal.Mono.Cecil.AssemblyDefinition]::ReadAssembly([IO.Path]::GetFullPath($BridgeAssemblyPath))
try {
    $water = $game.MainModule.Types | Where-Object FullName -eq 'Game.Simulation.WaterSystem'
    $surface = @($water.Methods | Where-Object Name -eq 'GetSurfaceData')
    if ($surface.Count -ne 1 -or !$surface[0].IsPublic -or $surface[0].ReturnType.FullName -ne 'Game.Simulation.WaterSurfaceData`1<Game.Simulation.SurfaceWater>') { throw 'Native water surface signature changed' }
    if (!($surface[0].Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq 'm_depthsReader' })) { throw 'Native surface getter no longer uses the depth reader' }
    $terrain = @($bridge.MainModule.Types | Where-Object Name -eq 'Mod' | ForEach-Object { $_.Methods } | Where-Object Name -eq 'Terrain')
    if ($terrain.Count -ne 1) { throw 'Compiled terrain adapter not found' }
    $calls = @($terrain[0].Body.Instructions | Where-Object { $_.OpCode.Name -in @('call','callvirt') } | ForEach-Object { $_.Operand.FullName })
    if (!($calls -match 'WaterSystem::GetSurfaceData\(') -or ($calls -match 'GetVelocitiesSurfaceData')) { throw 'Bridge is not using the full-precision depth surface' }
    foreach ($name in @('SampleDepth','SamplePolluted','SampleVelocity')) {
        if (!($calls -match ('WaterUtils::' + $name + '\('))) { throw "Native sampler missing: $name" }
    }
    $fields = @{
        'Game.Simulation.NaturalResourceCell' = @('m_Fertility','m_Ore','m_Oil','m_Fish')
        'Game.Simulation.NaturalResourceAmount' = @('m_Base','m_Used')
        'Game.Simulation.GroundWater' = @('m_Amount','m_Max','m_Polluted')
    }
    foreach ($name in $fields.Keys) {
        $type = $game.MainModule.Types | Where-Object FullName -eq $name
        foreach ($fieldName in $fields[$name]) {
            if (!($type.Fields | Where-Object { $_.Name -eq $fieldName -and $_.IsPublic })) { throw "Native resource field changed: $name.$fieldName" }
        }
    }
    foreach ($cell in @('NaturalResourceCell','GroundWater')) {
        if (!($calls -match ('CellMapSystem`1<Game.Simulation.' + $cell + '>::GetMap\('))) { throw "Compiled resource map read missing: $cell" }
    }
    Write-Output 'PASS: actual game water/resource contracts and compiled bridge sampler calls verified offline.'
} finally { $bridge.Dispose(); $game.Dispose() }
