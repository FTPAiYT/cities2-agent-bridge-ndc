param(
    [string]$SnapshotPath,
    [string]$OutputPath = (Join-Path $PSScriptRoot ('artifacts\city-map-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.html')),
    [double]$X, [double]$Z, [ValidateRange(16,500)][double]$Radius = 400
)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputPath){throw 'Choose a new output filename; existing maps are preserved.'}
if($SnapshotPath){$json=Get-Content -LiteralPath $SnapshotPath -Raw}
else {
    $reply=& (Join-Path $PSScriptRoot 'bridge.ps1') get_city_map -ArgsJson (@{x=$X;z=$Z;radius=$Radius}|ConvertTo-Json -Compress)
    $response=$reply|ConvertFrom-Json
    if(!$response.ok){throw ($reply -join [Environment]::NewLine)}
    $json=$reply -join [Environment]::NewLine
}
$null=$json|ConvertFrom-Json
$template=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'view-map.html') -Raw
$safeJson=$json.Replace('<','\u003c').Replace('>','\u003e').Replace('&','\u0026')
$html=$template.Replace('<script id="embedded" type="application/json">null</script>','<script id="embedded" type="application/json">'+$safeJson+'</script>')
[IO.File]::WriteAllText($OutputPath,$html)
Write-Output $OutputPath
