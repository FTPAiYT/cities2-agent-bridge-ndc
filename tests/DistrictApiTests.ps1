param(
    [string]$GamePath='C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II',
    [Parameter(Mandatory)][string]$BridgeAssemblyPath
)
$ErrorActionPreference='Stop'
$managed=Join-Path $GamePath 'Cities2_Data/Managed'
Add-Type -Path (Join-Path $managed 'Colossal.Mono.Cecil.dll')
$game=[Colossal.Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'Game.dll'))
$bridge=[Colossal.Mono.Cecil.AssemblyDefinition]::ReadAssembly([IO.Path]::GetFullPath($BridgeAssemblyPath))
try {
    $fields=@{
        'Game.Areas.Node'=@('m_Position')
        'Game.Areas.CurrentDistrict'=@('m_District')
        'Game.Areas.ServiceDistrict'=@('m_District')
        'Game.Citizens.HouseholdMember'=@('m_Household')
        'Game.Buildings.PropertyRenter'=@('m_Property')
        'Game.Citizens.HomelessHousehold'=@('m_TempHome')
        'Game.Citizens.Student'=@('m_School','m_Level')
        'Game.Prefabs.SchoolData'=@('m_StudentCapacity','m_EducationLevel')
    }
    foreach($name in $fields.Keys) {
        $type=$game.MainModule.Types | Where-Object FullName -eq $name
        foreach($field in $fields[$name]) {
            if(!($type.Fields | Where-Object { $_.Name -eq $field -and $_.IsPublic })){throw "Native atlas field missing: $name.$field"}
        }
    }
    $age=$game.MainModule.Types | Where-Object FullName -eq 'Game.Citizens.CitizenAge'
    $expected=@{Child=0;Teen=1;Adult=2;Elderly=3}
    foreach($name in $expected.Keys) {
        $field=$age.Fields | Where-Object Name -eq $name
        if(!$field -or $field.Constant -ne $expected[$name]){throw "Native age enum changed: $name"}
    }
    $mod=$bridge.MainModule.Types | Where-Object FullName -eq 'CitiesIIAgentBridge.Mod'
    $capture=$mod.Methods | Where-Object Name -eq 'CaptureDistrictAtlas'
    $calls=@($capture.Body.Instructions | Where-Object { $_.OpCode.Name -in @('call','callvirt') } | ForEach-Object { $_.Operand.FullName })
    foreach($pattern in @('Citizen::GetAge\(','Citizen::GetEducationLevel\(','UpgradeUtils::TryGetCombinedComponent<Game.Prefabs.SchoolData>','CompleteAllTrackedJobs')) {
        if(!($calls -match $pattern)){throw "Compiled atlas native read missing: $pattern"}
    }
    $nativeJob=($game.MainModule.Types | Where-Object FullName -eq 'Game.Tools.SelectionToolSystem').NestedTypes | Where-Object Name -eq 'UpdateServiceDistrictsJob'
    $nativeCalls=@(($nativeJob.Methods | Where-Object Name -eq 'Execute').Body.Instructions | Where-Object {$null -ne $_.Operand} | ForEach-Object {$_.Operand.ToString()})
    if(!($nativeCalls -match 'AddComponent<Game.Common.Updated>')){throw 'Native district assignment update contract changed'}
    $setter=$mod.Methods | Where-Object Name -eq 'SetServiceDistricts'
    $calls=@($setter.Body.Instructions | Where-Object { $_.OpCode.Name -in @('call','callvirt') } | ForEach-Object {$_.Operand.FullName})
    foreach($pattern in @('Mod::RequireControl\(','DistrictAssignment::CheckExpected\(','AddComponent<Game.Common.Updated>','DynamicBuffer`1<Game.Areas.ServiceDistrict>::Clear\(','DynamicBuffer`1<Game.Areas.ServiceDistrict>::Add\(')) {
        if(!($calls -match $pattern)){throw "Compiled assignment contract missing: $pattern"}
    }
    $areaTool=$game.MainModule.Types | Where-Object FullName -eq 'Game.Tools.AreaToolSystem'
    if($areaTool.IsSealed){throw 'Native area tool no longer supports the adapter subclass'}
    $definitions=@($areaTool.Methods | Where-Object Name -eq 'UpdateDefinitions')
    if($definitions.Count -ne 1 -or $definitions[0].ReturnType.FullName -ne 'Unity.Jobs.JobHandle' -or $definitions[0].Parameters.Count -ne 3){throw 'Native area definition method changed'}
    $signature=@($definitions[0].Parameters | ForEach-Object {$_.ParameterType.FullName})
    if(($signature -join '|') -ne 'Unity.Jobs.JobHandle|Unity.Collections.NativeArray`1<Unity.Entities.Entity>|Unity.Collections.NativeArray`1<Unity.Entities.Entity>'){throw 'Native area definition parameter types changed'}
    foreach($name in @('m_State','m_AllowCreateArea','m_ControlPointsMoved','m_ForceCancel','m_ApplyBlocked')) {
        if(!($areaTool.Fields | Where-Object Name -eq $name)){throw "Native area state field changed: $name"}
    }
    $configuration=$game.MainModule.Types | Where-Object FullName -eq 'Game.Prefabs.AreasConfigurationData'
    if(!(($configuration.Fields | Where-Object Name -eq 'm_DefaultDistrictPrefab').FieldType.FullName -eq 'Unity.Entities.Entity')){throw 'Default district prefab contract changed'}
    $drawing=$bridge.MainModule.Types | Where-Object FullName -eq 'CitiesIIAgentBridge.BridgeDistrictTool'
    if($drawing.BaseType.FullName -ne 'Game.Tools.AreaToolSystem'){throw 'Drawing is not a native area-tool adapter'}
    $instructions=@($drawing.Methods | Where-Object HasBody | ForEach-Object {$_.Body.Instructions})
    $drawingCalls=@($instructions | Where-Object {$_.OpCode.Name -in @('call','callvirt')} | ForEach-Object {$_.Operand.FullName})
    foreach($pattern in @('AreaToolSystem::GetControlPoints\(','AreaToolSystem::set_recreate\(','DistrictPreviewSafety::Validate\(','DistrictGeometry::Equivalent\(','NameSystem::SetCustomName\(')) {
        if(!($drawingCalls -match $pattern)){throw "Native district drawing/readback call missing: $pattern"}
    }
    if(!($instructions | Where-Object {$_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'UpdateDefinitions'})){throw 'Native area definitions are not invoked'}
    if($drawingCalls -match '(EntityManager|EntityCommandBuffer)::(AddBuffer|SetBuffer)<Game.Areas.Node>'){throw 'Drawing bypasses the native pipeline to write live boundaries'}
    $drawCommand=$mod.Methods | Where-Object Name -eq 'DrawDistrict'
    $drawCalls=@($drawCommand.Body.Instructions | Where-Object {$_.OpCode.Name -in @('call','callvirt')} | ForEach-Object {$_.Operand.FullName})
    foreach($pattern in @('Mod::RequireControl\(','DistrictGeometry::Validate\(','AreaUtils::GetMinNodeDistance\(','BridgeDistrictTool::Begin\(')) {
        if(!($drawCalls -match $pattern)){throw "Drawing preflight missing: $pattern"}
    }
    Write-Output 'PASS: native AreaToolSystem signature/state, default district prefab, guarded native drawing, preview gate and entity/polygon readback verified offline.'
    Write-Output 'PASS: native district fields, age bands, compiled census/upgraded capacity calls, guarded assignment and Updated notification verified offline.'
} finally {$bridge.Dispose();$game.Dispose()}
