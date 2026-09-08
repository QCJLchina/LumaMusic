# 一键发版脚本：commit -> tag -> push -> zip -> GitHub Release
# 用法: ./scripts/make-release.ps1 -Version 1.2.0 [-Notes "补充说明"]
# 前置: 已安装 gh 并登录；DOTNET_ROOT 指向 .tools/dotnet（bootstrap.py 已装好）
# 注意: pfx 私钥不上传；tag 在本地创建后随 push 同步，本地图谱与 GitHub 永远一致。
param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Notes = ""
)
$ErrorActionPreference = "Stop"
Set-Location "$PSScriptRoot\.."

# 0. 前置检查
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "未安装 GitHub CLI (gh)" }
git diff --quiet 2>$null; if (-not $?) { Write-Host "!! 工作区有未提交改动，先 commit"; exit 1 }
$dirty = git status --porcelain
if ($dirty) { Write-Host "!! 工作区有未提交文件:`n$dirty"; exit 1 }

# 1. 同步 csproj 版本号
$csproj = "app/LumaMusic.csproj"
$content = Get-Content $csproj -Raw
if ($content -match '<Version>([\d.]+)</Version>') {
    $content = $content -replace '<Version>[\d.]+</Version>', "<Version>$Version</Version>"
    Set-Content $csproj $content -NoNewline
    Write-Host "[1/6] csproj 版本 -> $Version"
}

# 2. 杀掉运行中的实例（exe 锁）
Get-Process LumaMusic -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Write-Host "[2/6] 已停止运行中的 LumaMusic"

# 3. 构建 + publish 自包含 zip
$env:DOTNET_ROOT = "$PWD\.tools\dotnet"
& .\.tools\dotnet\dotnet.exe publish app/LumaMusic.csproj -c Release -r win-x64 --self-contained -p:Platform=x64 -p:WindowsAppSDKSelfContained=true
if ($LASTEXITCODE -ne 0) { throw "publish 失败" }
$pub = "app\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish"
Copy-Item "build\native-bin\LumaAudio.dll" $pub -Force
Copy-Item "build\native-bin\LumaDsd.exe" $pub -Force
Write-Host "[3/6] publish 完成（含 LumaAudio.dll / LumaDsd.exe）"

# 4. 打 zip（与 CI/打包脚本统一放 dist/）
$zipName = "LumaMusic-win-x64.zip"
$stage = "build\release-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item "$pub\*" $stage -Recurse
Compress-Archive -Path "$stage\*" -DestinationPath "dist\$zipName" -Force
Write-Host "[4/6] zip 打包 -> dist\$zipName"

# 5. commit + 本地 tag + push（tag 随 push 上去，图谱同步）
git add -A
if (git diff --cached --quiet 2>$null) { Write-Host "[5/6] 无需 commit" }
else {
    git commit -m "v$Version：版本号升级"
    if (-not $?) { throw "commit 失败" }
}
git tag -f "v$Version"
git push origin main --tags
if (-not $?) { throw "push 失败" }
Write-Host "[5/6] commit + tag v$Version + push 完成"

# 6. GitHub Release（三件套统一从 dist/ 取：zip 来自第 4 步，msix/cer 来自 make-msix.ps1 输出）
$tag = "v$Version"
$notesFile = "build\release-notes.md"
$body = @"
## LumaMusic $Version

$Notes

### 安装
- **便携版**：下载 ``LumaMusic-win-x64.zip`` 解压运行 ``LumaMusic.exe``
- **MSIX**：先导入 ``LumaMusic.cer``（受信任的根证书颁发机构），再安装 ``LumaMusic.msix``，支持应用内自动更新
"@
Set-Content $notesFile $body -Encoding UTF8
$assets = @("dist\$zipName")
if (Test-Path "dist\LumaMusic.msix") { $assets += "dist\LumaMusic.msix" }
if (Test-Path "dist\LumaMusic.cer") { $assets += "dist\LumaMusic.cer" }
if ($assets.Count -eq 1) { Write-Host "!! dist/ 下没有 msix/cer，本次只发 zip（先跑 scripts/make-msix.ps1 再重跑本脚本第 6 步）" }
gh release create $tag $assets --title "LumaMusic $Version" --notes-file $notesFile
if (-not $?) { throw "gh release create 失败" }
Write-Host "[6/6] Release $tag 已发布（$($assets.Count) 个资产）"
Write-Host "完成: https://github.com/QCJLchina/LumaMusic/releases/tag/$tag"
