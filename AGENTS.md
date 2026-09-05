# LumaMusic

WinUI 3 无损音乐播放器：C#/XAML 前端 + C++/BASS 原生音频引擎（`LumaAudio.dll` 播放引擎、`LumaDsd.exe` DSD 解码工作进程）。

## 仓库布局

- `app/` — WinUI 3 前端（C#，`LumaMusic.csproj`）
- `native/` — C++ 音频引擎源码（`engine.cpp` 导出 luma_* API；`dsd_worker.cpp`）
- `tests/` — `native_tests.cpp`（CMake 目标 `LumaNativeTests`）、`audio_smoke.py`（真实解码/输出冒烟测试，样本为自生成的数字静音）
- `scripts/` — `bootstrap.py`（下载 pinned 依赖）、`prepare_native.py`（vendor/sacd → build/sacd 打补丁）、`build-native.ps1`、`build.ps1`、`run-dev.ps1`
- `vendor/` `build/` `.tools/` `.downloads/` `.packages/` `dist/` — 全部可由脚本重建，已在 .gitignore 中

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

## 协作约定

- 提交信息用中文祈使句，小步提交
- 多智能体并行开发用分支或 `git worktree` 隔离；不要并发写同一个 `build/ui-test-profile`
