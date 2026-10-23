# LumaMusic v2

Bit-Perfect Native Audio & Lyrics Reimagined // Dual-Engine Music Player

[简体中文](README.md) | [English](README.en.md)

[![License: AGPL-3.0](https://img.shields.io/badge/License-AGPL--3.0-blue.svg)](LICENSE)
[![GitHub release](https://img.shields.io/github/v/release/QCJLchina/LumaMusic?label=release)](https://github.com/QCJLchina/LumaMusic/releases)
[![Node.js](https://img.shields.io/badge/node-%3E%3D24-339933?logo=node.js&logoColor=white)](https://nodejs.org/)

## Overview

**LumaMusic v2** is a generational leap from LumaMusic 1.0. It bridges 1.0's ultra-high-performance **C++20 / BASS / ASIO / DSD bit-perfect native lossless audio engine** with a state-of-the-art **Electron + React + WebGPU/WebGL immersive lyric visualizer**:

1. **Audiophile Bit-Perfect Output**: WASAPI Exclusive, direct ASIO driver bypass, DSD-to-PCM / DoP passthrough / Native DSD straight to the DAC without Windows resampling; native support for SACD ISO, DSF, DFF, APE, FLAC, WAV, and CUE splitting.
2. **Kinetic Typography & Shaders**: Over 10 full-screen immersive lyric animation themes, AI palette generation, dynamic tri-color ambient glow, and Smart Beat mode.
3. **Multi-Source Aggregation**: Local high-res library management seamlessly integrated with online providers (NetEase, QQ Music, KuGou, Bodian, YouTube Music, Spotify, Navidrome).

---

## Key Features

- **Bit-Perfect Audio Engine**: C++20 / BASSWASAPI / BASSASIO hardware-aligned output.
- **Full DSD & High-Res Support**: DSF, DFF, SACD ISO (with DST decoding), DoP and ASIO Native DSD.
- **Accurate CUE Splitting & True Seek**: Split cue sheets with microsecond-precise boundary truncation; hardware handles stay held during seeks to prevent device takeover.
- **ReplayGain**: Applied to PCM float samples without touching DSD bitstreams.
- **Full-Screen Animations**: 10+ visualizer themes with customizable typography and motion.
- **Smart Beat Mode**: Audio-reactive motion powered by Beat This! and HTDemucs AI models.

---

## Architecture

```
LumaMusic v2
├─ native/luma-audio/    C++20 native audiophile audio engine source & CMake
│   ├─ engine.cpp        BASS / WASAPI / ASIO ring-buffered playback engine
│   └─ dsd_worker.cpp    DSD decoding worker (DSF/DFF/ISO -> DSD/PCM)
├─ electron/
│   ├─ lumaAudioHost.cjs FFI dynamic library bridge (koffi zero-overhead calls)
│   ├─ main.cjs          Main process lifecycle, audio devices, and IPC channels
│   └─ preload.cjs       Context-isolated secure API bridge
└─ src/                  React application, Zustand state, and WebGL/WebGPU visualizer stage
```

---

## Download & Installation

Download the latest Windows x64 installer (`LumaMusic-Setup-2.0.0.exe`) from the [Releases page](https://github.com/QCJLchina/LumaMusic/releases/latest).

---

## License

Licensed under [AGPL-3.0](LICENSE). Native BASS libraries are copyright [un4seen](https://www.un4seen.com/).
