param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II')
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GamePath 'Cities2_Data\Managed'
Add-Type -Path (Join-Path $managed 'Colossal.Mono.Cecil.dll')
$assembly = [Colossal.Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'Game.dll'))
try {
    $lockedType = $assembly.MainModule.Types | Where-Object FullName -eq 'Game.Prefabs.Locked'
    if (!$lockedType -or !($lockedType.Interfaces | Where-Object { $_.InterfaceType.FullName -eq 'Unity.Entities.IEnableableComponent' })) {
        throw 'Prefab Locked contract changed: expected an enableable component.'
    }
    Write-Output 'Prefab Locked enableable-component contract verified.'
    $checks = @{
        'Game.Tools.NetToolSystem' = @{ Methods = @('SnapControlPoints','UpdateCourse'); Fields = @('m_Prefab') }
        'Game.Tools.ZoneToolSystem' = @{ Methods = @('UpdateDefinitions'); Fields = @('m_RaycastPoint','m_StartPoint','m_State','m_SnapPoint') }
        'Game.Tools.ObjectToolSystem' = @{ Methods = @('SnapControlPoint','UpdateDefinitions'); Fields = @('m_ControlPoints','m_Rotation','m_MovingObject','m_MovingInitialized','m_UpgradingObject','m_TransformPrefab','m_Prefab') }
        'Game.Tools.BulldozeToolSystem' = @{ Methods = @('UpdateDefinitions'); Fields = @('m_ControlPoints') }
    }
    foreach ($name in $checks.Keys) {
        $type = $assembly.MainModule.Types | Where-Object FullName -eq $name
        if (!$type -or $type.IsSealed) { throw "Native tool cannot be derived: $name" }
        foreach ($method in $checks[$name].Methods) {
            $matchesFound = @($type.Methods | Where-Object Name -eq $method)
            if ($matchesFound.Count -ne 1 -or $matchesFound[0].ReturnType.FullName -ne 'Unity.Jobs.JobHandle') { throw "Native method contract changed: $name.$method" }
        }
        foreach ($field in $checks[$name].Fields) {
            if (!($type.Fields | Where-Object Name -eq $field)) { throw "Native field missing: $name.$field" }
        }
    }
    Write-Output 'Native construction adapter members verified against installed Game.dll.'
    $objectTool=$assembly.MainModule.Types | Where-Object FullName -eq 'Game.Tools.ObjectToolSystem'
    $stateTypes=@{m_MovingObject='Unity.Entities.Entity';m_MovingInitialized='Unity.Entities.Entity';m_UpgradingObject='Unity.Entities.Entity';m_Prefab='Game.Prefabs.ObjectPrefab';m_TransformPrefab='Game.Prefabs.TransformPrefab'}
    foreach($name in $stateTypes.Keys) {
        $field=$objectTool.Fields | Where-Object Name -eq $name
        if($field.FieldType.FullName -ne $stateTypes[$name]){throw "Object placement state type changed: $name"}
    }
    Write-Output 'Object placement state reset field types verified.'
    $rotation=$objectTool.NestedTypes | Where-Object Name -eq 'Rotation'
    foreach($field in @('m_Rotation','m_ParentRotation','m_IsAligned','m_IsSnapped')) {
        if(!($rotation.Fields | Where-Object Name -eq $field)){throw "Object snapping rotation contract changed: $field"}
    }
    $happiness=$assembly.MainModule.Types | Where-Object FullName -eq 'Game.Simulation.CitizenHappinessSystem'
    foreach($field in @('m_LastDeps','m_HappinessFactors')){if(!($happiness.Fields | Where-Object Name -eq $field)){throw "Happiness diagnostics contract changed: $field"}}
    Write-Output 'Rotation and happiness diagnostics native contracts verified.'
    $modSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\Mod.cs') -Raw
    $clientSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bridge.ps1') -Raw
    $commands = @([regex]::Matches($modSource, 'case "([a-z_]+)":') | ForEach-Object { $_.Groups[1].Value })
    $declared = @((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'commands.json') -Raw | ConvertFrom-Json))
    foreach ($command in $commands) {
        if (!$clientSource.Contains("'$command'") -or $command -notin $declared) { throw "Command is unreachable or undocumented: $command" }
    }
    if (($commands | Sort-Object -Unique).Count -ne $commands.Count) { throw 'Duplicate command handlers' }
    Write-Output "Client/dispatcher/catalog contract verified: $($commands.Count) commands."
} finally { $assembly.Dispose() }
