param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools/dotnet'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { throw 'Run bootstrap.py and build the native engine first.' }
$profile = 'build/ui-regression-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
& $Python tests/make_ui_fixture.py --profile $profile
if ($LASTEXITCODE) { throw 'UI fixture generation failed (requires Pillow).' }
& $dotnet build app/LumaMusic.csproj --no-restore -c Release -p:Platform=x64 -p:LumaUiRegression=true
if ($LASTEXITCODE) { throw 'UI regression build failed.' }
$savedDataDir = $env:LUMA_DATA_DIR
$savedRegression = $env:LUMA_UI_REGRESSION
$savedExit = $env:LUMA_UI_REGRESSION_EXIT
try {
    $env:LUMA_DATA_DIR = Join-Path $projectRoot $profile
    $env:LUMA_UI_REGRESSION = '1'
    $env:LUMA_UI_REGRESSION_EXIT = '1'
    $app = Join-Path $projectRoot 'app/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/LumaMusic.exe'
    $testProcess = Start-Process -FilePath $app -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
    if (!$testProcess.WaitForExit(90000)) { Stop-Process -Id $testProcess.Id; throw 'UI regression timed out.' }
    $reportPath = Join-Path $env:LUMA_DATA_DIR 'ui-regression.json'
    if (!(Test-Path -LiteralPath $reportPath)) { throw "Missing report; inspect $env:LUMA_DATA_DIR/luma.log" }
    $checks = [IO.File]::ReadAllText($reportPath) | ConvertFrom-Json
    $failures = @($checks | Where-Object { !$_.passed })
    Write-Output "UI regression: $($checks.Count - $failures.Count)/$($checks.Count) passed. Report: $reportPath"
    if ($failures.Count) { $failures | ConvertTo-Json -Depth 8; throw 'UI regression failed.' }
} finally {
    $env:LUMA_DATA_DIR = $savedDataDir
    $env:LUMA_UI_REGRESSION = $savedRegression
    $env:LUMA_UI_REGRESSION_EXIT = $savedExit
}
