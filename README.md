# LumaMusic

Windows 无损音乐播放器：WinUI 3 前端 + C++/BASS 原生音频引擎，面向位完美（bit-perfect）回放。

## 特性

- **格式**：FLAC / APE / ALAC / Opus / WavPack / WAV，以及 **DSF / DFF / SACD ISO**（内置 DSD 解码工作进程，支持 DST 解码）
- **DSD 回放**：DSD 转 PCM、DoP 透传、ASIO 原生 DSD（Native DSD）
- **输出后端**：WASAPI 共享 / WASAPI 独占 / ASIO，采样率可跟随音源或强制指定
- **按设备记忆配置**：每台输出设备独立保存输出方式、DSD 模式、通道映射等档案
- **ASIO 通道映射**：多声道输出可自定义 ASIO 通道顺序
- **歌词与封面**：联网搜索歌词/专辑封面、本地 LRC 导入、逐行动画歌词
- **媒体库**：文件夹导入、播放列表、收藏、播放队列、上次播放记忆
- **界面**：玻璃拟态风格、环境光背景（Win2D）、可减少动态效果

## 系统要求

- Windows 10 2004（build 19041）或更高，x64
- 无需安装 .NET 运行时与 Windows App SDK（自包含发布）

## 从源码构建

需要 Python 3（仅用于依赖引导）与 Visual Studio 2022（含 CMake、MSVC v143）。

```powershell
python scripts/bootstrap.py        # 下载 pinned 依赖（BASS SDK、SACD 解码源码、.NET SDK）到本地
powershell scripts/build-native.ps1  # 构建 C++ 原生引擎（LumaAudio.dll / LumaDsd.exe）并跑原生测试
powershell scripts/build.ps1         # 构建 WinUI 3 应用
powershell scripts/run-dev.ps1       # 以独立测试档案启动开发版
```

所有第三方依赖均为构建时按 pinned 地址下载，不随仓库分发。

## 架构

```
app/     WinUI 3（C#，net10.0-windows10.0.19041.0，x64）
native/  C++ 音频引擎
         ├ LumaAudio.dll  播放引擎（C 导出 API：设备枚举 / 打开 / 状态 / 音量）
         └ LumaDsd.exe    DSD 解码工作进程（DSF/DFF/ISO → DSD 或 PCM 流）
```

前端通过 P/Invoke 调用 `LumaAudio.dll`；引擎内部基于 BASS / BASSWASAPI / BASSASIO / BASSMIX，DSD 解码复用 [Kodi audiodecoder.sacd](https://github.com/xbmc/audiodecoder.sacd)（Piers 分支）。

## 说明

- 本项目为个人项目，仅供学习交流；BASS 库版权归 [un4seen](https://www.un4seen.com/) 所有，由构建脚本从官方下载，本仓库不包含其任何文件。
- ASIO 播放需要系统已安装对应驱动；对免驱 USB DAC，可使用厂商 ASIO 驱动或 ASIO4ALL 等通用驱动。
