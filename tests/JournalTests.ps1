$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('CitiesIIAgentJournalTests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$transcript=Join-Path $root 'transcript.jsonl'
$rows=@(
 @{timestamp='2026-09-05T21:00:00Z';type='event_msg';payload=@{type='item_completed';item=@{type='AgentMessage';id='visible';phase='commentary';content=@(@{type='Text';text='School capacity checked. <script>unsafe</script>'})}}},
 @{timestamp='2026-09-05T21:00:01Z';type='event_msg';payload=@{type='item_completed';item=@{type='AgentMessage';id='hidden';phase='analysis';content=@(@{type='Text';text='PRIVATE_SENTINEL'})}}},
 @{timestamp='2026-09-05T21:00:02Z';type='response_item';payload=@{type='reasoning';summary='PRIVATE_SENTINEL'}},
 @{timestamp='2026-09-05T21:00:03Z';type='event_msg';payload=@{type='item_completed';item=@{type='UserMessage';content=@(@{type='Text';text='USER_SENTINEL'})}}}
)
@($rows|ForEach-Object {$_|ConvertTo-Json -Depth 15 -Compress})|Set-Content $transcript
Add-Content $transcript '{"partial":'
$journal=Join-Path $PSScriptRoot '..\journal.ps1'
$result=& $journal start -Title 'Test <title>' -TranscriptPath $transcript -JournalRoot (Join-Path $root 'journals')|ConvertFrom-Json
if($result.entries -ne 1){throw 'Visible-message allowlist failed'}
$sync=& $journal sync -JournalRoot (Join-Path $root 'journals')|ConvertFrom-Json
if($sync.entries -ne 1){throw 'Import duplicated entries'}
$html=Get-Content $result.html -Raw
if($html -match 'PRIVATE_SENTINEL|USER_SENTINEL|<script>unsafe'){throw 'Excluded data or unescaped HTML leaked'}
if($html -notmatch '&lt;script&gt;unsafe'){throw 'Visible text missing'}
$note=& $journal note -Text 'Verified result.' -JournalRoot (Join-Path $root 'journals')|ConvertFrom-Json
if($note.entries -ne 2){throw 'Manual note missing'}
$finish=& $journal finish -JournalRoot (Join-Path $root 'journals')|ConvertFrom-Json
if($finish.active -or (Test-Path (Join-Path $root 'journals\active.json'))){throw 'Finish did not deactivate journal'}
'PASS: visible-only extraction, malformed tail, deduplication, HTML escaping, manual notes, finish. No game commands sent.'
Copy-Item (Join-Path $PSScriptRoot '..\bridge.ps1') $root
Copy-Item $journal $root
$box=Join-Path $root 'mailbox'
New-Item -ItemType Directory -Path "$box\requests","$box\responses"|Out-Null
@{status='ready';heartbeatUtc=[DateTime]::UtcNow.ToString('O');session='fake';citySession='fake-city'}|ConvertTo-Json|Set-Content "$box\session.json"
& "$root\journal.ps1" start -Title 'Mock bridge session'|Out-Null
$job=Start-Job -ArgumentList $box -ScriptBlock {
 param($box)
 $until=[DateTime]::UtcNow.AddSeconds(12)
 while([DateTime]::UtcNow -lt $until){
  $request=Get-ChildItem "$box\requests" -Filter '*.json'|Select-Object -First 1
  if($request){$r=Get-Content $request.FullName -Raw|ConvertFrom-Json; @{ok=$true;result=@{status='queued';id='fake-op'}}|ConvertTo-Json -Compress|Set-Content "$box\responses\$($r.id).json";return}
  Start-Sleep -Milliseconds 50
 }
}
try{
 $reply=& "$root\bridge.ps1" ping -MailboxPath $box|ConvertFrom-Json
 if(!$reply.ok -or $reply.result.status -ne 'queued'){throw 'Journal altered bridge response'}
 $active=Get-Content "$root\journals\active.json" -Raw|ConvertFrom-Json
 $records=@(Get-Content "$($active.folder)\entries.jsonl"|ForEach-Object {$_|ConvertFrom-Json})
 if($records.Count -ne 2 -or $records[1].details.result.status -ne 'queued'){throw 'Request/response journal missing or inaccurate'}
 if(@(Get-ChildItem "$box\requests" -Filter '*.json').Count -ne 1){throw 'Command was replayed'}
 'PASS: mock bridge records sent and queued outcomes, preserves response, sends once.'
}finally{Stop-Job $job;Remove-Job $job}
