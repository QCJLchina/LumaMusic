# NSIS 安装器打包：packaging/LumaMusic.nsi + dist/LumaMusic 发布产物 → dist/LumaMusic-Setup.exe。
# 与 make-msix.ps1 对齐：以 -Version 注入版本号，产物落 dist/，失败即抛错。
# 关键坑 1：.nsi 里的中文默认按系统 ANSI 码页解析，中文（快捷方式名）会变乱码；
#           必须 /INPUTCHARSET UTF8，且 .nsi 存 UTF-8 无 BOM。
# 关键坑 2：makensis 默认装在 C:\Program Files (x86)\NSIS，不在 PATH；CI 上若缺失由 workflow 先装。
# 关键坑 3：脚本文件必须带 BOM（含中文），否则 PS 5.1 按 GBK 解析本脚本自身的中文注释与消息。
param([string]$Version = '1.1.4', [string]$Makensis = '')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$nsi=Join-Path $root 'packaging/LumaMusic.nsi'
$dist=Join-Path $root 'dist/LumaMusic'
$output=Join-Path $root 'dist/LumaMusic-Setup.exe'
$produced=Join-Path $root 'packaging/LumaMusic-Setup.exe'

if(-not $Makensis){
    $candidates=@(
        (Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe'),
        (Join-Path $env:ProgramFiles 'NSIS\makensis.exe')
    )
    $Makensis=$candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if(-not $Makensis -or -not (Test-Path -LiteralPath $Makensis)){throw 'makensis 未找到：装 NSIS 或传 -Makensis <路径>'}
if(-not (Test-Path -LiteralPath $dist)){throw "publish output missing: $dist (先跑 scripts/build.ps1 -Publish)"}
if(-not (Test-Path -LiteralPath $nsi)){throw "installer script missing: $nsi"}
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw "版本号格式应为 x.y.z，收到：$Version"}

# makensis 不支持带 BOM 的 UTF-8 输入，先挡掉，避免报出难以定位的语法错误
$bytes=[IO.File]::ReadAllBytes($nsi)
if($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF){throw "$nsi 带 UTF-8 BOM，makensis 解析会失败；请另存为 UTF-8 无 BOM"}

if(Test-Path -LiteralPath $output){Remove-Item -LiteralPath $output -Force}
if(Test-Path -LiteralPath $produced){Remove-Item -LiteralPath $produced -Force}
& $Makensis /V2 /INPUTCHARSET UTF8 "/DVERSION=$Version" $nsi
if($LASTEXITCODE){throw "makensis failed (exit $LASTEXITCODE)"}
# OutFile 是相对 .nsi 解析的（NSIS 的现行工作目录），搬回 dist/ 保持发布资产路径稳定
if(Test-Path -LiteralPath $produced){Move-Item -LiteralPath $produced -Destination $output -Force}
if(-not (Test-Path -LiteralPath $output)){throw "installer not produced: $output"}
$size=(Get-Item -LiteralPath $output).Length
if($size -lt 20MB){throw "installer suspiciously small: $size bytes"}
Write-Output ("installer: " + $output + " (" + [math]::Round($size/1MB,1) + " MB, version $Version)")

