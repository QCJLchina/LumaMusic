# LumaMusic

WinUI 3 无损音乐播放器：C#/XAML 前端 + C++/BASS 原生音频引擎（`LumaAudio.dll` 播放引擎、`LumaDsd.exe` DSD 解码工作进程）。

## 仓库布局

- `app/` — WinUI 3 前端（C#，`LumaMusic.csproj`）
- `native/` — C++ 音频引擎源码（`engine.cpp` 导出 luma_* API；`dsd_worker.cpp`）
- `tests/` — `native_tests.cpp`（CMake 目标 `LumaNativeTests`）、`audio_smoke.py`（真实解码/输出冒烟测试，样本为自生成的数字静音）
- `scripts/` — `bootstrap.py`（下载 pinned 依赖）、`prepare_native.py`（vendor/sacd → build/sacd 打补丁）、`build-native.ps1`、`build.ps1`、`run-dev.ps1`
- `vendor/` `build/` `.tools/` `.downloads/` `.packages/` `dist/` — 全部可由脚本重建，已在 .gitignore 中
- FlexASIO 通用 ASIO 驱动安装器由 `bootstrap.py` 按 pinned 版本下载到 `vendor/flexasio/`，经 csproj Content 条目随应用发布包分发（开箱 ASIO 独占，见 `app/Services/AsioSetup.cs`）

## 环境引导（fresh clone 后）

```powershell
python scripts/bootstrap.py        # 下载 pinned BASS SDK/SACD 源码/dotnet SDK 到 vendor/ .tools/ .downloads/
powershell scripts/build-native.ps1  # prepare_native.py → CMake → build/native-bin → 自动跑 LumaNativeTests
powershell scripts/build.ps1         # 应用构建（bundled dotnet，环境变量脚本已处理）
```

## 构建注意

- 重建前先 `taskkill /IM LumaMusic.exe /F`，否则 exe 被锁构建失败
- 直接调 dotnet 时必须用项目自带 SDK：设 `DOTNET_ROOT=$PWD/.tools/dotnet` 后执行 `./.tools/dotnet/dotnet.exe build app/LumaMusic.csproj -c Release -p:Platform=x64`
- 开发运行用 `scripts/run-dev.ps1`：自动拷原生 DLL 到 app/bin、以 `LUMA_DATA_DIR=build/ui-test-profile` 启动

## 测试与调试

- `build/ui-test-profile` 是测试档案（library.db/缓存/日志），不要写入用户真实 `%LOCALAPPDATA%`
- UI 崩溃看 `build/ui-test-profile/luma.log`（`App.UnhandledException` 未设 Handled，任何 UI 异常都会闪退）
- 无 GUI 验证：PowerShell PrintWindow 抓窗口 PNG；音频链路可 PowerShell P/Invoke `LumaAudio.dll` 直接调 luma_* API 真实出声验证

## WinUI 3 已知坑（踩过）

- `x:Bind` string→ImageSource 必须走静态函数 `Track.CoverImage`：`XamlBindingHelper.ConvertValue` 对空串/null 抛 ArgumentException 且未处理即崩溃
- `CanvasAnimatedControl` 是密封类，无法继承
- 控件 `Collapsed` 时 Win2D 资源创建/Uri 加载会静默失败（AmbientBackdrop 因此用"首帧 Pump"模式）

## MSIX 打包与发版

- 打包一条命令：`powershell scripts/make-msix.ps1 -Version x.y.z.0`（依赖 `build/msix-layout/` + `dist/LumaMusic` 发布产物 + `build/make-cert.ps1` 的自签证书）。发版三件套：zip（绿色版）+ msix + cer，`gh release create/upload` 上传。
- **打包身份下 MRT Core 只认包根 `resources.pri`**（未打包模式才认 `LumaMusic.pri`）——手工打 MSIX 漏了它，应用装上后秒退且无任何崩溃事件/转储，这是"装上打不开"的第一嫌疑。脚本每次从 `LumaMusic.pri` 复制生成，勿删。
- PS 5.1 编码坑（本项目实测踩过）：无 BOM 的 .ps1 按 GBK 解析（含中文的脚本必须带 BOM）；`Get-Content` 对无 BOM 的 UTF-8 文件按 GBK 读，含中文的 AppxManifest 会被毁（乱码+吃掉闭合引号，XML 报错位置还会错位误导）——读写 manifest 用 `[IO.File]::ReadAllText/WriteAllText`；正则替换串 `'$1'+数字` 被 .NET 当成更大组号，必须 `${1}`。
- AppxManifest 的 `BackgroundColor` 合法格式是 6 位 hex（如 `#101918`），8 位 ARGB 反而 schema 报错。
- Git Bash 调 makeappx/signtool/reg 等必须加 `MSYS_NO_PATHCONV=1`，否则 `/d` `/o` 之类开关被路径转换弄坏。
- MSIX 版与绿色版数据共享 `%LOCALAPPDATA%\LumaMusic`（full-trust 包不虚拟化该目录）；用户装 MSIX 前需先导入 `.cer` 到受信任的根证书颁发机构。

## 协作约定

- 提交信息用中文祈使句，小步提交
- **推送到远端必须由用户手动控制**：agent 只做本地 commit，`git push` / 强推 / 删除远端分支等任何上远端的动作都要先征得用户明确同意；完成后注明"已提交未推送"
- 多智能体并行开发用分支或 `git worktree` 隔离；不要并发写同一个 `build/ui-test-profile`
