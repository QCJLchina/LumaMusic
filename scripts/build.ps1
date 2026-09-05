param([switch]$Publish)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
python scripts/make-icon.py
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.tools/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:NUGET_PACKAGES=Join-Path $projectRoot '.packages'
$dotnet=Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if($Publish){& $dotnet publish app/LumaMusic.csproj -c Release -o dist/LumaMusic -p:Platform=x64 *> build/app-build.log}
else{& $dotnet build app/LumaMusic.csproj -c Release -p:Platform=x64 *> build/app-build.log}
Get-Content build/app-build.log | Select-Object -Last 28
if($LASTEXITCODE){throw 'Application build failed'}
