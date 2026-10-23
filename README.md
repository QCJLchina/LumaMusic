# LumaMusic v2

专注于极致位完美无损音频与绚丽全屏歌词动画的双引擎音乐播放器 // Bit-Perfect Native Audio & Lyrics Reimagined

[简体中文](README.md) | [English](README.en.md)

[![License: AGPL-3.0](https://img.shields.io/badge/License-AGPL--3.0-blue.svg)](LICENSE)
[![GitHub release](https://img.shields.io/github/v/release/QCJLchina/LumaMusic?label=release)](https://github.com/QCJLchina/LumaMusic/releases)
[![Node.js](https://img.shields.io/badge/node-%3E%3D24-339933?logo=node.js&logoColor=white)](https://nodejs.org/)

## 项目简介

**LumaMusic v2** 是 LumaMusic 1.0 的划时代重构版本。它将 1.0 版本极高性能的 **C++20 / BASS / ASIO / DSD 位完美原生无损音频引擎**，与新一代现代化 **Electron + React + WebGPU/WebGL 绚丽全屏动效界面** 深度融合：

1. **硬件级发烧回放**：保留 LumaMusic 经典的 WASAPI 独占、ASIO 驱动直通、DSD 转 PCM / DoP 透传 / 原生 Native DSD，位完美不经过系统混音重采样；完整支持 SACD ISO、DSF、DFF、APE、FLAC、WAV 与 CUE 拆轨。
2. **文字 PV 级视觉沉浸**：十余款全屏沉浸歌词动画模式（浮名、流光、心象、云阶、群唱、倾诉、镜台、时计、花火等），AI 配色主题生成，支持节拍驱动的智能动感模式与三色动态环境光。
3. **全平台音源聚合**：本地高解析无损音乐库与在线音乐（网易云、QQ 音乐、酷狗、波点、YouTube Music、Spotify、Navidrome）无缝集成。

---

## 核心特性

| 模块 | 说明 |
| --- | --- |
| **原生位完美引擎** | 基于 C++20 / BASS / BASSWASAPI / BASSASIO 构建，支持 WASAPI 共享 / 独占与 ASIO 原生驱动，采样率与硬件时钟严格对齐，不重采样。 |
| **高解析与 DSD 全覆盖** | 内置 DSD 解码工作进程（Kodi audiodecoder.sacd + libdstdec），支持 **SACD ISO**、**DSF**、**DFF**（含 DST 解码），提供 DSD 转 PCM、DoP 与 Native DSD 三种输出模式。 |
| **CUE 拆轨与真 Seek** | 单文件整轨 + 同名 `.cue` 自动拆分多首，首尾精准时间截断无欠载爆音；Seek 时仅重建解码器，**不释放硬件独占句柄**，避免设备被其他应用抢占。 |
| **ReplayGain 增益** | 支持按设备配置关闭 / 专辑增益 / 歌曲增益，增益仅乘在 PCM 浮点样本上，**绝不改动 DSD 原始字节流**，坚守位完美。 |
| **全屏歌词动效** | 浮名、流光、心象、云阶、群唱、倾诉、镜台、时计、花火等，支持响应式排版与丰富调参。 |
| **智能动感模式** | 基于 Beat This! 与 HTDemucs AI 模型，歌词与舞台元素可直接跟随鼓点与节拍实时跳动。 |
| **多源歌曲与歌词匹配** | 本地音乐自动匹配在线歌词与封面，支持 LRC、VTT、TTML、QRC、YRC、KRC 歌词解析与导出。 |

---

## 展示

![visualizer](./img/visualizer.png)

### 部分主题预览

<table>
  <tr>
    <td width="50%"><img src="./img/preview-fume.png" alt="浮名 主题预览" /></td>
    <td width="50%"><img src="./img/preview-lumi.png" alt="流光 主题预览" /></td>
  </tr>
  <tr>
    <td align="center"><strong>浮名</strong></td>
    <td align="center"><strong>流光</strong></td>
  </tr>
  <tr>
    <td width="50%"><img src="./img/preview-cad.png" alt="心象 主题预览" /></td>
    <td width="50%"><img src="./img/preview-pat.png" alt="云阶 主题预览" /></td>
  </tr>
  <tr>
    <td align="center"><strong>心象</strong></td>
    <td align="center"><strong>云阶</strong></td>
  </tr>
  <tr>
    <td width="50%"><img src="./img/preview-diorama.png" alt="镜台 主题预览" /></td>
    <td width="50%"><img src="./img/preview-pendolo.png" alt="时计 主题预览" /></td>
  </tr>
  <tr>
    <td align="center"><strong>镜台</strong></td>
    <td align="center"><strong>时计</strong></td>
  </tr>
</table>

---

## 架构

```
LumaMusic v2
├─ native/luma-audio/    C++20 原生高保真音频引擎源码与 CMake 构建工程
│   ├─ engine.cpp        BASS / WASAPI / ASIO 播放引擎与环形缓冲
│   └─ dsd_worker.cpp    DSD 解码工作进程（DSF/DFF/ISO → DSD/PCM）
├─ electron/
│   ├─ lumaAudioHost.cjs FFI 动态库桥接（koffi 零编译开销调用）
│   ├─ main.cjs          主进程生命周期、硬件设备与 IPC 通道
│   └─ preload.cjs       安全上下文隔离暴露 API
└─ src/                  React 前端应用、Zustand 状态管理与 WebGL/WebGPU 动效舞台
```

---

## 快速下载与安装

- 前往 [Releases 页面](https://github.com/QCJLchina/LumaMusic/releases/latest) 下载最新的 Windows 安装包（如 `LumaMusic-Setup-2.0.0.exe`）。
- 双击安装即可使用，支持 Windows 10 (2004+) 及 Windows 11 x64。

---

## 本地开发与构建

### 环境要求
- Node.js >= 24.0.0
- npm >= 11.0.0
- CMake 与 MinGW-w64 / MSVC（仅二次开发 C++ 原生引擎时需要；日常前端与 Electron 开发无需编译 C++）

### 安装与启动
```bash
# 安装依赖
npm install

# 启动开发服务器（网页预览）
npm run dev

# 启动 Electron 桌面开发版
npm run dev:electron

# 检查类型与代码规范
npm run typecheck

# 构建 Windows x64 安装包
npm run build && npx electron-builder --win --x64
```

---

## 许可证

本项目基于 [AGPL-3.0](LICENSE) 许可证开源。原音频引擎部分及第三方依赖保留相应许可证（BASS 归 un4seen 所有，Kodi audiodecoder.sacd 遵循 GPL）。
