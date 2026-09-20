param([string]$OutputDirectory, [switch]$NoRestore)
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot ('artifacts/verify-'+(Get-Date -Format yyyyMMdd-HHmmss))}
    if(Test-Path $OutputDirectory){throw 'Use a new output directory.'}
    New-Item -ItemType Directory $OutputDirectory | Out-Null
    $OutputDirectory=(Resolve-Path $OutputDirectory).Path
    [string[]]$restoreArguments = if ($NoRestore) { @('--no-restore') } else { @() }
    dotnet build DP.Vision.sln -c Release --nologo @restoreArguments
    if($LASTEXITCODE -ne 0){throw 'Build failed.'}
    dotnet test tests/DP.Vision.Tests -c Release --no-build --nologo
    if($LASTEXITCODE -ne 0){throw 'Portable tests failed.'}
    dotnet test tests/DP.Vision.Algorithms.Tests -c Release --no-build --nologo
    if($LASTEXITCODE -ne 0){throw 'Independent algorithm tests failed.'}
    dotnet test tests/DP.Vision.Halcon.Tests -c Release --no-build --nologo
    if($LASTEXITCODE -ne 0){throw 'HALCON boundary tests failed.'}
    & tools/DP.Vision.Probe/bin/Release/net48/DP.Vision.Probe.exe (Join-Path $OutputDirectory 'net48')
    if($LASTEXITCODE -ne 0){throw 'net48 native probe failed.'}
    dotnet tools/DP.Vision.Probe/bin/Release/net8.0-windows/DP.Vision.Probe.dll (Join-Path $OutputDirectory 'net8')
    if($LASTEXITCODE -ne 0){throw 'net8 native probe failed.'}
    foreach($runtime in @('net48','net8.0-windows')){foreach($ui in @('winforms','wpf')){foreach($mode in @('roi','results')){
        $prefix='demo-';if($mode -eq 'results'){$prefix='demo-results-'}
        $image=Join-Path $OutputDirectory ($prefix+$runtime+'-'+$ui+'.png')
        $demo=Join-Path $PSScriptRoot ("samples/DP.Vision.Demo/bin/Release/"+$runtime+'/DP.Vision.Demo')
        $arguments=@('--smoke',('"'+$image+'"'));if($ui -eq 'wpf'){$arguments=@('--wpf')+$arguments}
        if($mode -eq 'results'){$arguments=@('--results')+$arguments}
        if($runtime -eq 'net48'){$exe=$demo+'.exe'}else{$exe='dotnet';$arguments=@(('"'+$demo+'.dll"'))+$arguments}
        $process=Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
        $null=$process.Handle
        if(-not $process.WaitForExit(60000)){$process.Kill();throw 'Demo smoke timed out.'}
        $process.WaitForExit()
        if($process.ExitCode -ne 0 -or -not (Test-Path $image)){throw "Demo $mode smoke failed."}
    }}}
    Write-Output 'PASS: independent DP.Vision; both UI backends/frameworks; result browsers, ROI transactions and demo tooling. WPF physical input routing remains unverified.'
} finally {Pop-Location}
