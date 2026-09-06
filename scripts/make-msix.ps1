# MSIX 打包：从 dist/LumaMusic 同步 layout → 打包 → 签名。
# 关键坑 1：打包身份下 MRT Core 只认包根的 resources.pri（未打包模式才认 LumaMusic.pri），
#           缺了它 XAML 初始化直接失败、进程秒退——每次都从 LumaMusic.pri 复制出 resources.pri。
# 关键坑 2：PS5.1 的 -Encoding UTF8 带 BOM，makeappx 不认；manifest 必须无 BOM。
# 关键坑 3：正则替换串里 '$1'+数字 会被 .NET 当成更大的组号，必须写 ${1}/${2}。
# 关键坑 4：读 manifest 必须用 [IO.File]::ReadAllText——PS5.1 的 Get-Content 对无 BOM 的 UTF-8 按 GBK 读，中文会毁掉整个 manifest。
param([string]$Version = '1.0.2.0', [string]$PfxPassword = 'luma-msix')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$layout=Join-Path $root 'build/msix-layout'
$dist=Join-Path $root 'dist/LumaMusic'
$sdk=(Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory | Where-Object Name -match '^10\.' | Sort-Object Name | Select-Object -Last 1).FullName

if(-not (Test-Path $dist)){throw "publish output missing: $dist (先跑 scripts/build.ps1 -Publish)"}
Copy-Item -Path (Join-Path $dist '*') -Destination $layout -Recurse -Force
Copy-Item (Join-Path $layout 'LumaMusic.pri') (Join-Path $layout 'resources.pri') -Force
$manifestPath=Join-Path $layout 'AppxManifest.xml'
$raw=[IO.File]::ReadAllText($manifestPath)
if(-not $raw){throw 'AppxManifest.xml 为空，拒绝继续'}
$manifest=[regex]::Replace($raw,'(<Identity\s[^>]*?Version=")[^"]*(")',('${1}'+$Version+'${2}'))
[IO.File]::WriteAllText($manifestPath,$manifest,[Text.UTF8Encoding]::new($false))
if((Get-Item $manifestPath).Length -lt 200){throw 'AppxManifest.xml 写入后异常过短'}
Write-Output ("manifest: " + (Get-Item $manifestPath).Length + " bytes, version $Version")

$msix=Join-Path $root 'dist/LumaMusic.msix'
& "$sdk\x64\makeappx.exe" pack /d $layout /p $msix /o
if($LASTEXITCODE){throw 'makeappx failed'}
& "$sdk\x64\signtool.exe" sign /fd SHA256 /f (Join-Path $root 'dist/LumaMusic.pfx') /p $PfxPassword $msix
if($LASTEXITCODE){throw 'signtool failed'}
Write-Output "MSIX ready: $msix (version $Version)"
