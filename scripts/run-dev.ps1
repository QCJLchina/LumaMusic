$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$appDir=Join-Path $projectRoot 'app/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64'
Copy-Item -Path (Join-Path $projectRoot 'build/native-bin/*.dll') -Destination $appDir -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'build/native-bin/LumaDsd.exe') -Destination $appDir -Force
$env:LUMA_DATA_DIR=Join-Path $projectRoot 'build/ui-test-profile'
$p=Start-Process -FilePath (Join-Path $appDir 'LumaMusic.exe') -WorkingDirectory $appDir -WindowStyle Hidden -PassThru
Write-Output "Luma test process: $($p.Id)"
