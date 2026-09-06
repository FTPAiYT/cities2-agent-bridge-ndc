$ErrorActionPreference='Stop'
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('CitiesIIAgentBridge-client-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\advance.ps1') -Destination $testRoot
$mock=@'
param([string]$Command,[string]$ArgsJson,[int]$TimeoutSeconds)
Add-Content -LiteralPath (Join-Path $PSScriptRoot 'calls.txt') -Value $Command
$scenario=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'scenario.txt')
if($Command -eq 'simulate_step') {
 if($scenario -eq 'reject'){'{"ok":false,"error":"operation_in_progress"}';return}
 '{"ok":true,"result":{"id":"test-step","status":"running"}}';return
}
if($Command -eq 'cancel_simulation_step'){'{"ok":true,"result":{"status":"complete","paused":true}}';return}
if($scenario -eq 'error'){throw 'Synthetic polling failure'}
if($scenario -eq 'deadline'){'{"ok":true,"result":{"id":"test-step","status":"running"}}';return}
'{"ok":true,"result":{"id":"test-step","status":"complete","paused":true,"reason":"frame_limit"}}'
'@
Set-Content -LiteralPath (Join-Path $testRoot 'bridge.ps1') -Value $mock
foreach($scenario in @('success','reject','error','deadline')) {
 Set-Content -LiteralPath (Join-Path $testRoot 'scenario.txt') -Value $scenario
 Set-Content -LiteralPath (Join-Path $testRoot 'calls.txt') -Value ''
 $clock=[Diagnostics.Stopwatch]::StartNew();$failed=$false
 try{$reply=& (Join-Path $testRoot 'advance.ps1') -Frames 1 -WallSeconds 1 | ConvertFrom-Json}catch{$failed=$true}
 $calls=Get-Content -LiteralPath (Join-Path $testRoot 'calls.txt')
 $cancels=@($calls|Where-Object {$_ -eq 'cancel_simulation_step'}).Count
 if($scenario -eq 'success' -and ($failed -or !$reply.result.paused -or $cancels -ne 0)){throw 'Successful step did not return paused without extra cancellation.'}
 if($scenario -eq 'reject' -and (!$failed -or $cancels -ne 0)){throw 'Rejected request cancelled an unrelated step.'}
 if($scenario -in @('error','deadline') -and (!$failed -or $cancels -ne 1)){throw 'Client failure must request cancellation exactly once.'}
 if($clock.Elapsed.TotalSeconds -gt 10){throw 'Client deadline was not bounded.'}
 Write-Output "PASS: client $scenario ($([Math]::Round($clock.Elapsed.TotalSeconds,2)) seconds)"
}
Write-Output 'Four client scenarios passed using a fake mailbox client. No game commands were sent.'
foreach($name in @('view-map.html','export-map.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('..\'+$name)) -Destination $testRoot}
$snapshot=Join-Path $testRoot 'snapshot.json';$htmlPath=Join-Path $testRoot 'map.html'
$payload='</script><script>throw new Error("unexpected execution")</script>'
@{result=@{note=$payload;bounds=@{minX=0;maxX=200;minZ=0;maxZ=200}}}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $snapshot
$null=& (Join-Path $testRoot 'export-map.ps1') -SnapshotPath $snapshot -OutputPath $htmlPath
$html=Get-Content -LiteralPath $htmlPath -Raw
if($html.Contains($payload) -or !$html.Contains('\u003c/script\u003e')){throw 'Embedded map data was not escaped safely.'}
$embedded=[regex]::Match($html,'<script id="embedded" type="application/json">([\s\S]*?)</script>').Groups[1].Value|ConvertFrom-Json
if($embedded.result.note -ne $payload){throw 'Escaping changed map data.'}
Write-Output 'PASS: offline map export escapes embedded data and preserves its content.'
$overwriteRejected=$false
try{& (Join-Path $testRoot 'export-map.ps1') -SnapshotPath $snapshot -OutputPath $htmlPath}catch{$overwriteRejected=$true}
if(!$overwriteRejected){throw 'Map export overwrote an existing file.'}
Write-Output 'PASS: existing map export is preserved.'
