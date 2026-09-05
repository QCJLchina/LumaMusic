$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
python scripts/prepare_native.py
if ($LASTEXITCODE) { throw 'Native preparation failed' }
$vsRoot = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products '*' -property installationPath
$cmakePath = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
& $cmakePath -S . -B build/native -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE) { throw 'CMake configuration failed' }
& $cmakePath --build build/native --config Release --parallel 4 *> build/native-build.log
if ($LASTEXITCODE) { Get-Content build/native-build.log | Select-String 'error ' | Select-Object -First 18; throw 'Native build failed; see build/native-build.log' }
foreach ($dependency in @('bass','bassasio','basswasapi','bassmix','bassflac','bassape','bassalac','bassopus','basswv')) {
    Copy-Item -LiteralPath "vendor/$dependency/x64/$dependency.dll" -Destination 'build/native-bin' -Force
}
& build/native-bin/LumaNativeTests.exe
if ($LASTEXITCODE) { throw 'Native tests failed' }
