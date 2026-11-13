// native/luma-audio/spectrum_contract.h
// GENERATED FILE - do not edit. Source: src/services/analyserContract.ts
// Regenerate with: npm run native:spectrum-contract
//
// 这四个值必须和网页 AnalyserNode 完全一致，否则共享模式与独占模式下的可视化会不一致。

#pragma once

// FFT 点数；frequencyBinCount 是它的一半。
constexpr unsigned SPECTRUM_FFT = 2048u;
constexpr unsigned SPECTRUM_BINS = SPECTRUM_FFT / 2;
// 帧间平滑系数。
constexpr float SPECTRUM_SMOOTHING = 0.6f;
// getByteFrequencyData 的 dBFS 映射区间。
constexpr float SPECTRUM_MIN_DB = -100.0f;
constexpr float SPECTRUM_MAX_DB = -30.0f;
