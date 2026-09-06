# 第三方组件声明（Third-Party Notices）

本仓库与发布包中包含/使用的第三方组件：

## FlexASIO（随应用分发）

- **用途**：通用 ASIO 驱动，供"输出方式 → ASIO"开箱即用；本应用将其配置为 WASAPI 独占模式
- **版本**：1.10b（pinned，SHA-512 见 `scripts/bootstrap.py`）
- **版权**：Copyright © Etienne Dechamps
- **许可证**：GNU General Public License v3.0（GPLv3）
- **源码**：https://github.com/dechamps/FlexASIO
- **分发形式**：未修改的官方安装器 `FlexASIOSetup.exe`，随应用发布包分发，由用户在应用内主动触发安装。本应用不修改、不静态链接 FlexASIO 的任何部分。
- FlexASIO 基于PortAudio（http://www.portaudio.com/），其许可证随 FlexASIO 源码仓库一并提供。

## BASS 音频库（构建时下载，不随仓库/发布包再分发）

- **用途**：`LumaAudio.dll` 的音频引擎基础库（bass/basswasapi/bassasio/bassmix 及解码插件）
- **版权**：Copyright © un4seen.com. All rights reserved.
- **来源**：https://www.un4seen.com/ —— 由 `scripts/bootstrap.py` 按 pinned 地址下载，本仓库与发布包均不包含其任何文件，遵守其再分发条款。

## audiodecoder.sacd（构建时下载，不随仓库/发布包再分发）

- **用途**：SACD ISO / DSF / DFF 的 DSD 解码参考实现（编译进 `LumaDsd.exe`）
- **来源**：https://github.com/xbmc/audiodecoder.sacd （Piers 分支），GPL 许可证族；由构建脚本按 pinned 地址下载源码并打补丁编译，本仓库与发布包不包含其源码。
