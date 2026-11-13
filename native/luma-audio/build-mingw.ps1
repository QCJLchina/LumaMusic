# native/luma-audio/build-mingw.ps1
# 用 MinGW-w64 构建 LumaAudio.dll，并把结果放回 electron/resources/audio-bin/win-x64。
#
# 为什么需要这个脚本：CMakeLists.txt 面向 MSVC（/utf-8、CMAKE_MSVC_RUNTIME_LIBRARY），
# 但在没有 MSVC 的机器上仍然要能产出可用的 DLL。那条 g++ 命令原先只存在于终端历史里，
# 换个人就得重新摸索一遍——而参数错了不会在编译期提示：缺 -static 会依赖 libwinpthread-1.dll，
# 临时目录含中文会报 "can't create *.o"。
#
# 用法：powershell -NoProfile -File native/luma-audio/build-mingw.ps1 [-Deploy]
#   不加 -Deploy 只编译到 native/luma-audio/build/native-bin。
# 有 MSVC 的发布机器上应改用 CMakeLists.txt 的正式构建，两边产物等价即可。
# 这里只用 Windows PowerShell 5.1 也支持的写法，不要求 pwsh 7。
#
# 注意：本文件必须保存为 UTF-8 with BOM。Windows PowerShell 5.1 读取无 BOM 的 UTF-8 时按系统
# 代码页解码，下面这些中文注释会变成乱码并连带破坏语法分析（表现为莫名其妙的 ParseException）。
# 很多工具在改写文件时会静默丢掉 BOM（编辑器「另存为 UTF-8」、脚本化替换、某些补丁工具），
# 所以下面有自检；改完这个文件请确认它以 EF BB BF 开头。

[CmdletBinding()]
param(
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'

# BOM 自检必须放在最前面，而且只能用纯 ASCII 写：文件一旦丢了 BOM，下面所有中文注释在
# PowerShell 5.1 眼里都是乱码，语法分析会失败并指向一条语法完全正确的行。那时用户看到的报错
# 与真实原因毫无关系，所以先自己检查一遍。读自身字节，前 3 字节不是 EF BB BF 就退出。
try {
    $selfPath = $PSCommandPath
    if ($selfPath -and (Test-Path -LiteralPath $selfPath)) {
        $selfBytes = [System.IO.File]::ReadAllBytes($selfPath)
        if ($selfBytes.Length -lt 3 -or $selfBytes[0] -ne 0xEF -or $selfBytes[1] -ne 0xBB -or $selfBytes[2] -ne 0xBF) {
            Write-Host 'build-mingw.ps1: this file lost its UTF-8 BOM. Windows PowerShell 5.1 decodes a BOM-less file with the system codepage, which mangles the Chinese comments and breaks parsing.'
            Write-Host 'Re-save it as "UTF-8 with BOM" (e.g. in VS Code: change encoding to UTF-8 with BOM), or strip the Chinese comments.'
            exit 1
        }
    }
} catch {
    # 读不到自身时不要因此失败：自检只是提示，真正的构建还在后面。
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent (Split-Path -Parent $here)
$outDir = Join-Path $here 'build\native-bin'
$audioBin = Join-Path $root 'electron\resources\audio-bin\win-x64'

# 每次编译前先从唯一来源生成频谱契约。否则有人只改 analyserContract.ts，
# 会得到一份能编译、但频段定义仍停留在旧值的 DLL，构建过程不会有任何提示。
$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
if (-not $nodeCommand) {
    throw 'node not found. It is required to regenerate native/luma-audio/spectrum_contract.h before compiling.'
}
$generator = Join-Path $root 'dev\native\genSpectrumContract.mjs'
& $nodeCommand.Source $generator
if ($LASTEXITCODE -ne 0) { throw "Spectrum contract generation failed with exit code $LASTEXITCODE" }

# g++ 无法在含中文/非 ASCII 的临时目录里创建 .o 文件（"Assembler messages: can't create ..."），
# 所以把 TMP/TEMP 指到 ASCII 路径再编译，不改机器环境变量。
$tempDir = Join-Path $here 'build\tmp'
New-Item -ItemType Directory -Force -Path $tempDir, $outDir | Out-Null
$env:TMP = $tempDir
$env:TEMP = $tempDir

$gxxCommand = Get-Command g++ -ErrorAction SilentlyContinue
if (-not $gxxCommand) {
    throw 'g++ not found. This script needs a MinGW-w64 toolchain (x86_64-w64-mingw32). On a machine with MSVC, build with CMakeLists.txt instead.'
}
$gxx = $gxxCommand.Source

$vendor = Join-Path $here 'vendor'
$dllPath = Join-Path $outDir 'LumaAudio.dll'

$compileArgs = @(
    '-std=c++20', '-O2', '-shared',
    '-DNOMINMAX', '-DWIN32_LEAN_AND_MEAN', '-D_CRT_SECURE_NO_WARNINGS',
    '-o', $dllPath, 'engine.cpp',
    '-I.', '-Ivendor/bass/c', '-Ivendor/basswasapi/c', '-Ivendor/bassasio/c', '-Ivendor/bassmix/c',
    (Join-Path $vendor 'bass\c\x64\bass.lib'),
    (Join-Path $vendor 'bassasio\c\x64\bassasio.lib'),
    (Join-Path $vendor 'basswasapi\c\x64\basswasapi.lib'),
    (Join-Path $vendor 'bassmix\c\x64\bassmix.lib'),
    # 静态链接 C++ 运行库：否则产物依赖 libwinpthread-1.dll / libstdc++-6.dll，
    # 而 audio-bin 目录只放 BASS 的几个 DLL，加载时会直接失败。
    '-static', '-static-libgcc', '-static-libstdc++'
)

Push-Location $here
try {
    & $gxx @compileArgs
    if ($LASTEXITCODE -ne 0) { throw "g++ failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

if (-not (Test-Path $dllPath)) { throw "Expected $dllPath to exist after the build." }

# 断言新导出在场：漏掉 luma_spectrum 时 koffi 会在绑定这一步抛错，
# 那会让整个原生引擎不可用，而不只是少一个频谱。
$nmCommand = Get-Command nm -ErrorAction SilentlyContinue
if ($nmCommand) {
    $symbols = & $nmCommand.Source -g --defined-only $dllPath 2>&1
    if (-not ($symbols | Select-String -Quiet 'luma_spectrum')) {
        throw 'LumaAudio.dll is missing the luma_spectrum export.'
    }
}

if ($Deploy) {
    Copy-Item $dllPath (Join-Path $audioBin 'LumaAudio.dll') -Force
    Write-Host "Deployed to $audioBin"
}
Write-Host "Built $dllPath ($((Get-Item $dllPath).Length) bytes)"
