// src/services/analyserContract.ts
// 可视化频谱的数值契约：网页 AnalyserNode 与原生引擎两侧必须使用同一组数值。
//
// 这是**唯一来源**：`npm run native:spectrum-contract` 从本文件生成
// native/luma-audio/spectrum_contract.h，engine.cpp 直接包含那个头文件。所以两侧读的是同一份，
// 不再依赖「改这里也要记得改那里」的注释——那种约定在跨语言时从来没被守住过。
//
// 改了下面任何一个数字，都要重新生成头文件并重编 DLL：
//   npm run native:spectrum-contract && native/luma-audio/build-mingw.ps1 -Deploy
// 生成脚本会从本文件的 `export const NAME = <数字>;` 里抓值，所以请保持这个写法。

/** FFT 点数。`AnalyserNode.frequencyBinCount` 是它的一半，即 1024 个 bin。 */
export const ANALYSER_FFT_SIZE = 2048;

/** 帧间平滑系数。 */
export const ANALYSER_SMOOTHING = 0.6;

/** `getByteFrequencyData` 的 dBFS 映射区间。 */
export const ANALYSER_MIN_DB = -100;
export const ANALYSER_MAX_DB = -30;

/**
 * 一个 bin 覆盖多少 Hz。
 *
 * 两边的 bin 都均分 0..sampleRate/2，所以宽度就是 `sampleRate / fftSize`；而 `fftSize` 等于
 * `binCount * 2`。网页分析器和引擎帧都走这一个公式，不再各自写死 21.5 或 2048——
 * 写死的那两个数只在 44.1k + fftSize 2048 下才对。
 */
export const analyserBinHz = (sampleRate: number, binCount: number): number =>
    binCount > 0 ? (sampleRate || 44100) / (binCount * 2) : 0;
