# 深色界面预览截图：把真实窗口摆成 tests/UiRegression.cs 的 LUMA_UI_PREVIEW 阶段，逐阶段抓 PNG。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\preview-dark-ui.ps1 -Variant before
#       之后跑 scripts\measure-dark-contrast.py build\preview\*.png 得到文字区对比度指标。
# 需要先构建回归版：dotnet build app/LumaMusic.csproj -c Release -p:Platform=x64 -p:LumaUiRegression=true
# 编码：本脚本必须保持纯 ASCII（无 BOM 的 UTF-8 会被 Windows PowerShell 按 GBK 解析）。
param(
    [Parameter(Mandatory = $true)][string]$Variant,
    [string]$Profile = 'build/ui-preview',
    [string]$OutDir = 'build/preview',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$profilePath = Join-Path $root $Profile
$outPath = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $outPath | Out-Null
$exe = Join-Path $root 'app/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/LumaMusic.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "preview exe missing: $exe (build with -p:LumaUiRegression=true)" }
if (-not (Test-Path -LiteralPath $profilePath)) {
    & $Python (Join-Path $root 'tests/make_ui_fixture.py') --profile $Profile
    if ($LASTEXITCODE) { throw 'UI fixture generation failed (requires Pillow).' }
}

Add-Type -Namespace Luma -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

function Show-InFront([IntPtr]$Hwnd) {
    [void][Luma.Native]::ShowWindow($Hwnd, 9)
    [void][Luma.Native]::SetForegroundWindow($Hwnd)
    Start-Sleep -Milliseconds 400
    if ([Luma.Native]::GetForegroundWindow() -ne $Hwnd) {
        # Foreground rights can be denied; a brief topmost bump makes the window visible for the grab.
        [void][Luma.Native]::SetWindowPos($Hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x13)
        Start-Sleep -Milliseconds 400
    }
}

function Hide-Topmost([IntPtr]$Hwnd) {
    [void][Luma.Native]::SetWindowPos($Hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x13)
}

function Measure-Mean([System.Drawing.Bitmap]$bitmap) {
    $sum = 0.0; $count = 0
    for ($y = 0; $y -lt $bitmap.Height; $y += 17) {
        for ($x = 0; $x -lt $bitmap.Width; $x += 17) {
            $c = $bitmap.GetPixel($x, $y); $sum += ($c.R + $c.G + $c.B) / 3.0; $count++
        }
    }
    if ($count -eq 0) { return 0 }
    return $sum / $count
}

function Find-Window([uint32]$TargetPid) {
    $script:found = [IntPtr]::Zero
    $callback = [Luma.Native+EnumWindowsProc]{
        param($hwnd, $param)
        $windowPid = 0
        [void][Luma.Native]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
        if ($windowPid -eq $TargetPid -and [Luma.Native]::IsWindowVisible($hwnd)) {
            $rect = New-Object Luma.Native+RECT
            [void][Luma.Native]::GetWindowRect($hwnd, [ref]$rect)
            if (($rect.Right - $rect.Left) -gt 400 -and ($rect.Bottom - $rect.Top) -gt 400) { $script:found = $hwnd; return $false }
        }
        return $true
    }
    [void][Luma.Native]::EnumWindows($callback, [IntPtr]::Zero)
    return $script:found
}

function Save-Shot([IntPtr]$Hwnd, [string]$File) {
    $rect = New-Object Luma.Native+RECT
    [void][Luma.Native]::GetWindowRect($Hwnd, [ref]$rect)
    $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { throw 'window rect is empty' }
    # DWM stops composing a fully occluded window, so put it in front before every shot.
    Show-InFront $Hwnd
    Start-Sleep -Milliseconds 900
    [void][Luma.Native]::GetWindowRect($Hwnd, [ref]$rect)
    $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    $printed = [Luma.Native]::PrintWindow($Hwnd, $hdc, 2)
    $graphics.ReleaseHdc($hdc); $graphics.Dispose()
    $mean = Measure-Mean $bitmap
    if (-not $printed -or $mean -lt 6 -or $mean -gt 200) {
        # PrintWindow came back blank (all black or all white): grab the screen instead.
        $bitmap.Dispose()
        $bitmap = New-Object System.Drawing.Bitmap($width, $height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
        $graphics.Dispose()
        $mean = Measure-Mean $bitmap
    }
    Hide-Topmost $Hwnd
    $bitmap.Save($File, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Output ('shot ' + (Split-Path $File -Leaf) + ' mean=' + [math]::Round($mean, 1) + ' printed=' + $printed)
}

Get-ChildItem -LiteralPath $profilePath -Filter 'preview-*.txt' -ErrorAction SilentlyContinue | Remove-Item -Force
$env:LUMA_DATA_DIR = $profilePath
$env:LUMA_UI_REGRESSION = '1'
$env:LUMA_UI_PREVIEW = '1'
$process = Start-Process -FilePath $exe -WorkingDirectory $root -PassThru
try {
    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 160 -and $hwnd -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 250
        $hwnd = Find-Window ([uint32]$process.Id)
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw 'main window never appeared' }
    Write-Output ('window handle: ' + $hwnd)
    # 阶段顺序必须与 tests/UiRegression.cs 的 RunUiPreview 一致：错过一个 ack，驱动端会等满超时再往下走。
    foreach ($stage in @('library', 'now-white-lyrics', 'now-white-info', 'now-vivid-lyrics', 'paused', 'static-white', 'static-vivid')) {
        $marker = Join-Path $profilePath ('preview-' + $stage + '.txt')
        for ($i = 0; $i -lt 480 -and -not (Test-Path -LiteralPath $marker); $i++) { Start-Sleep -Milliseconds 250 }
        if (-not (Test-Path -LiteralPath $marker)) { throw "stage marker missing: $marker" }
        Write-Output ('stage ' + $stage + ' state ' + (Get-Content -LiteralPath $marker -Raw).Trim())
        Start-Sleep -Milliseconds 900
        Save-Shot $hwnd (Join-Path $outPath ($Variant + '-' + $stage + '.png'))
        New-Item -ItemType File -Force -Path (Join-Path $profilePath ('preview-' + $stage + '.ack')) | Out-Null
    }
    for ($i = 0; $i -lt 120 -and -not $process.HasExited; $i++) { Start-Sleep -Milliseconds 250; $process.Refresh() }
    if (Test-Path -LiteralPath (Join-Path $profilePath 'preview-error.txt')) {
        Write-Warning ('preview error marker: ' + (Get-Content -LiteralPath (Join-Path $profilePath 'preview-error.txt') -Raw))
    }
}
finally {
    $process.Refresh()
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    Remove-Item Env:LUMA_UI_PREVIEW -ErrorAction SilentlyContinue
    Remove-Item Env:LUMA_UI_REGRESSION -ErrorAction SilentlyContinue
    Remove-Item Env:LUMA_DATA_DIR -ErrorAction SilentlyContinue
}
Get-ChildItem -LiteralPath $outPath -Filter ($Variant + '-*.png') | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Output
