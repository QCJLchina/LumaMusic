# MSIX 打包：packaging/ 模板 + dist/LumaMusic 发布产物 → 清空重建 layout → 打包 → 签名。
# 关键坑 1：打包身份下 MRT Core 只认包根的 resources.pri（未打包模式才认 LumaMusic.pri），
#           缺了它 XAML 初始化直接失败、进程秒退——每次都从 LumaMusic.pri 复制出 resources.pri。
# 关键坑 2：PS5.1 的 -Encoding UTF8 带 BOM，makeappx 不认；manifest 必须无 BOM。
# 关键坑 3：正则替换串里 '$1'+数字 会被 .NET 当成更大的组号，必须写 ${1}/${2}。
# 关键坑 4：读 manifest 必须用 [IO.File]::ReadAllText——PS5.1 的 Get-Content 对无 BOM 的 UTF-8 按 GBK 读，中文会毁掉整个 manifest。
# 密码：取 -PfxPassword 参数或环境变量 LUMA_PFX_PASSWORD（CI secrets / 本地打包前设置）。
param([string]$Version = '1.0.2.0', [string]$PfxPassword = $env:LUMA_PFX_PASSWORD)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$packaging=Join-Path $root 'packaging'
$layout=Join-Path $root 'build/msix-layout'
$dist=Join-Path $root 'dist/LumaMusic'
$sdk=(Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory | Where-Object Name -match '^10\.' | Sort-Object Name | Select-Object -Last 1).FullName

if(-not (Test-Path $dist)){throw "publish output missing: $dist (先跑 scripts/build.ps1 -Publish)"}
$pfxPath=Join-Path $root 'dist/LumaMusic.pfx'
if(-not $PfxPassword){throw '签名密码缺失：传 -PfxPassword 或设环境变量 LUMA_PFX_PASSWORD'}
if(-not (Test-Path $pfxPath)){throw "签名证书缺失：$pfxPath（新机器从密码管理器取，或用 packaging/make-cert.ps1 重建）"}

# 每次清空重建 layout，避免上次打包残留文件混进包里
if(Test-Path $layout){Remove-Item $layout -Recurse -Force}
New-Item $layout -ItemType Directory | Out-Null
Copy-Item (Join-Path $packaging 'AppxManifest.xml') $layout
Copy-Item (Join-Path $packaging 'Assets') (Join-Path $layout 'Assets') -Recurse
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
& "$sdk\x64\signtool.exe" sign /fd SHA256 /f $pfxPath /p $PfxPassword $msix
if($LASTEXITCODE){throw 'signtool failed'}
Write-Output "MSIX ready: $msix (version $Version)"
