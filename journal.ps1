param(
    [ValidateSet('start','sync','note','event','finish')][string]$Command='sync',
    [string]$Title='Cities II Agent build journal',
    [string]$TranscriptPath,
    [string]$Text,
    [string]$EventJson,
    [string]$JournalRoot=(Join-Path $PSScriptRoot 'journals')
)
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force -Path $JournalRoot | Out-Null
$JournalRoot=[IO.Path]::GetFullPath($JournalRoot)
$activePath=Join-Path $JournalRoot 'active.json'
$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($JournalRoot)))
$mutex=[Threading.Mutex]::new($false,"Local\CitiesIIAgentJournal-$hash")
$locked=$false
function Write-Atomic($path,$value){
    $tmp="$path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($tmp,$value,[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($tmp,$path,$true)
}
function Escape-Html($value){[Net.WebUtility]::HtmlEncode([string]$value)}
try {
    $locked=$mutex.WaitOne(10000)
    if(!$locked){throw 'Journal is busy; no game command was retried.'}
    if($Command -eq 'start'){
        if(Test-Path $activePath){throw 'Finish the active journal before starting another.'}
        if($TranscriptPath){$TranscriptPath=(Resolve-Path -LiteralPath $TranscriptPath).Path}
        $folder=Join-Path $JournalRoot ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
        New-Item -ItemType Directory -Path $folder|Out-Null
        $cfg=[ordered]@{title=$Title;folder=$folder;transcript=$TranscriptPath;startedUtc=[DateTime]::UtcNow.ToString('O')}
        Write-Atomic $activePath ($cfg|ConvertTo-Json)
    }
    if(!(Test-Path $activePath)){return}
    $cfg=Get-Content -LiteralPath $activePath -Raw|ConvertFrom-Json
    $dataPath=Join-Path $cfg.folder 'entries.jsonl'
    $entries=[Collections.Generic.List[object]]::new()
    $seen=[Collections.Generic.HashSet[string]]::new()
    if(Test-Path $dataPath){foreach($l in Get-Content -LiteralPath $dataPath){if($l){$e=$l|ConvertFrom-Json;$entries.Add($e);[void]$seen.Add($e.id)}}}
    # Explicit allowlist: only completed, user-visible assistant messages. Never reasoning, summaries, or tools.
    if($cfg.transcript){
        $stream=[IO.File]::Open($cfg.transcript,'Open','Read','ReadWrite')
        $reader=[IO.StreamReader]::new($stream)
        try {while($null -ne ($line=$reader.ReadLine())){
            try{$r=$line|ConvertFrom-Json -Depth 80}catch{continue}
            if($r.type -ne 'event_msg' -or $r.payload.type -ne 'item_completed'){continue}
            $item=$r.payload.item
            if($item.type -ne 'AgentMessage' -or $item.phase -notin @('commentary','final')){continue}
            $id='chat:'+ $item.id
            if(!$seen.Add($id)){continue}
            $body=(@($item.content|Where-Object {$_.type -eq 'Text'}|ForEach-Object {$_.text}) -join "`n").Trim()
            if(!$body){continue}
            $entries.Add([ordered]@{id=$id;utc=$r.timestamp;kind=$item.phase;source='Visible chat message';text=$body})
        }} finally{$reader.Dispose()}
    }
    if($Command -in @('note','event')){
        $kind='commentary';$source='Authored journal note';$details=$null
        if($Command -eq 'event'){$details=$EventJson|ConvertFrom-Json -Depth 30;$Text=$details.text;$kind='bridge';$source='Bridge client'}
        if([string]::IsNullOrWhiteSpace($Text)){throw 'A journal entry needs text.'}
        $entries.Add([ordered]@{id=[guid]::NewGuid().ToString('N');utc=[DateTime]::UtcNow.ToString('O');kind=$kind;source=$source;text=$Text;details=$details})
    }
    $ordered=@($entries|Sort-Object utc,id)
    Write-Atomic $dataPath ((@($ordered|ForEach-Object {$_|ConvertTo-Json -Depth 35 -Compress}) -join "`n")+"`n")
    $md=[Text.StringBuilder]::new()
    [void]$md.AppendLine("# $($cfg.title)`n`nVisible commentary and recorded bridge events. No private reasoning. Original chat timestamps are UTC; historical messages describe what was known then.`n")
    $cards=[Text.StringBuilder]::new()
    foreach($e in $ordered){
        [void]$md.AppendLine("## $($e.utc) — $($e.kind)`n`n$($e.text)`n")
        $t=Escape-Html $e.text;$stamp=Escape-Html $e.utc;$kind=Escape-Html $e.kind
        $tag=if($e.kind -eq 'bridge'){'details'}else{'article'}
        $inner=if($tag -eq 'details'){"<summary>$stamp · Bridge event</summary><p>$t</p>"}else{"<header>$stamp · $kind</header><p>$t</p>"}
        [void]$cards.AppendLine("<$tag>$inner</$tag>")
    }
    Write-Atomic (Join-Path $cfg.folder 'journal.md') $md.ToString()
    $titleHtml=Escape-Html $cfg.title
    $html=@"
<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>$titleHtml</title>
<style>body{background:#111;color:#eee;font:20px/1.65 system-ui;margin:0 auto;max-width:960px;padding:32px}h1{font-size:30px}header,summary,.sub{color:#aaa;font-size:14px}article,details{border-top:1px solid #333;padding:22px 0}p{white-space:pre-wrap;margin:12px 0}label{cursor:pointer}summary{cursor:pointer}input{accent-color:#72debc}</style>
<h1>$titleHtml</h1><div class="sub">Visible build commentary · UTC timestamps · $($ordered.Count) entries<br>Historical updates are preserved as written, not revalidated claims.<br><label><input id="live" type="checkbox"> Refresh every 5 seconds</label></div>
$cards
<script>const k=location.pathname;const c=document.getElementById('live');c.checked=sessionStorage.getItem(k+'live')==='1';c.onchange=()=>sessionStorage.setItem(k+'live',c.checked?'1':'0');scrollTo(0,Number(sessionStorage.getItem(k+'scroll')||0));setInterval(()=>{if(c.checked){sessionStorage.setItem(k+'scroll',scrollY);location.reload()}},5000);</script></html>
"@
    Write-Atomic (Join-Path $cfg.folder 'journal.html') $html
    if($Command -eq 'finish'){Remove-Item -LiteralPath $activePath}
    [pscustomobject]@{entries=$ordered.Count;html=(Join-Path $cfg.folder 'journal.html');markdown=(Join-Path $cfg.folder 'journal.md');active=($Command -ne 'finish')}|ConvertTo-Json -Compress
}finally{if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}
