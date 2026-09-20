param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path $OutputDirectory){throw 'Use a new output directory.'}
New-Item -ItemType Directory $OutputDirectory | Out-Null
$OutputDirectory=(Resolve-Path $OutputDirectory).Path
$env:PATH="$env:HALCONROOT\bin\$env:HALCONARCH;"+$env:PATH
$exe=(Resolve-Path (Join-Path $PSScriptRoot '../../DP.LabelInspection/tools/DP.LabelInspection.CanvasBenchmark/bin/Release/net48/DP.LabelInspection.CanvasBenchmark.exe')).Path
foreach($round in 1,2){
    $backends=if($round -eq 1){@('halcon','unified','vision','vision-lod')}else{@('vision-lod','vision','unified','halcon')}
    foreach($case in @('image_4mp','image_16mp','region_100k','xld_100k','mixed')){foreach($backend in $backends){
        $prefix=Join-Path $OutputDirectory "$case-$backend-r$round"
        Write-Output "RUN $case $backend r$round"
        $process=Start-Process $exe -ArgumentList @($backend,$case,('"'+$prefix+'"')) -PassThru -RedirectStandardOutput ($prefix+'.log') -RedirectStandardError ($prefix+'.error.log')
        $null=$process.Handle
        if(-not $process.WaitForExit(120000)){$process.Kill();throw "Timeout $prefix"}
        $process.WaitForExit()
        if($process.ExitCode -ne 0){throw "Failed $prefix"}
        Start-Sleep -Milliseconds 350
    }}
}
Write-Output 'PASS 40 fresh-process comparisons.'
