[CmdletBinding(DefaultParameterSetName='SavedPages')]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory,ParameterSetName='SavedPages')][string]$PagesPath,
    [Parameter(Mandatory,ParameterSetName='Capture')][switch]$Capture,
    [Parameter(ParameterSetName='Capture')][string]$MailboxPath = (Join-Path $env:LOCALAPPDATA 'CitiesIIAgentBridge')
)
$ErrorActionPreference='Stop'
$destination=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $destination){throw 'Choose a new export directory; existing exports are preserved.'}
$nodeCommand=Get-Command node -ErrorAction Stop
$temporaryPages=$null
try {
    if($Capture) {
        $pages=[Collections.Generic.List[object]]::new()
        $snapshotId=$null
        foreach($layer in @('districts','buildings','roads')) {
            $offset=0
            do {
                $requestArgs=@{layer=$layer;offset=$offset;limit=512}
                if($snapshotId){$requestArgs.snapshotId=$snapshotId}
                $text=& (Join-Path $PSScriptRoot 'bridge.ps1') get_district_atlas -ArgsJson ($requestArgs|ConvertTo-Json -Compress) -MailboxPath $MailboxPath -TimeoutSeconds 45
                $reply=($text -join [Environment]::NewLine)|ConvertFrom-Json
                if(!$reply.ok){throw ('Atlas capture failed: '+($text -join [Environment]::NewLine))}
                $page=$reply.result
                if(!$page.snapshotId -or !$page.complete){throw 'Incomplete atlas snapshot'}
                if($snapshotId -and $snapshotId -ne $page.snapshotId){throw 'Snapshot changed during export'}
                $snapshotId=$page.snapshotId
                if($page.layer -ne $layer -or $page.offset -ne $offset){throw 'Unexpected atlas page'}
                $pages.Add($page)
                $next=$page.nextOffset
                if($null -ne $next -and $next -le $offset){throw 'Non-advancing atlas pagination'}
                $offset=$next
            } while($null -ne $next)
        }
        $temporaryPages=Join-Path ([IO.Path]::GetTempPath()) ('district-atlas-'+[guid]::NewGuid().ToString('N')+'.json')
        [IO.File]::WriteAllText($temporaryPages,(ConvertTo-Json -InputObject $pages.ToArray() -Depth 100))
        $PagesPath=$temporaryPages
    }
    & $nodeCommand.Source (Join-Path $PSScriptRoot 'atlas/export.mjs') ([IO.Path]::GetFullPath($PagesPath)) $destination
    if($LASTEXITCODE -ne 0){throw 'Atlas export validation or rendering failed'}
    Write-Output $destination
} finally {
    if($temporaryPages -and (Test-Path -LiteralPath $temporaryPages)){Remove-Item -LiteralPath $temporaryPages}
}
