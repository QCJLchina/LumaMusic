// dev/native/genSpectrumContract.mjs
// 从 src/services/analyserContract.ts 生成 native/luma-audio/spectrum_contract.h。
//
// 为什么要有这一步：频谱的四个数值（FFT 点数、平滑系数、dBFS 上下限）必须让网页分析器与原生
// 引擎用同一份。两侧各写一份字面量时，共享模式与独占模式下的动画会悄悄呈现不同的频段分布，
// 而「两种输出方式看起来一样」正是这套频谱存在的前提。注释约束不住漂移，只有生成能。
//
// 生成的文件不要手改：改 src/services/analyserContract.ts，再跑
//   npm run native:spectrum-contract
// engine.cpp 直接包含这个头文件，所以两侧读的是同一个来源。

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const sourcePath = path.join(root, 'src', 'services', 'analyserContract.ts');
const targetPath = path.join(root, 'native', 'luma-audio', 'spectrum_contract.h');

/**
 * 从 TS 源码里取出一个 `export const NAME = <number>;` 的数值。
 *
 * 这里故意用正则而不是 TypeScript AST：生成器只需要 Node 内置模块，不引入编译器依赖。
 * 代价是它只接受上面那种写法；报错必须把这个限制说清楚，否则类型标注或 `as const`
 * 之类正常改动会被误判成「导出不存在」。
 */
const readNumber = (source, name) => {
    const match = new RegExp(`export const ${name}\\s*=\\s*(-?[0-9.]+)\\s*;`).exec(source);
    if (!match) {
        throw new Error(
            `Could not read ${name} from analyserContract.ts. Expected exactly `
            + '`export const NAME = <number>;`. Type annotations and `as const` are not supported '
            + 'by this dependency-free generator; keep that form or switch readNumber to a TypeScript AST parser.',
        );
    }
    const value = Number(match[1]);
    if (!Number.isFinite(value)) throw new Error(`analyserContract.ts has a non-finite ${name}`);
    return value;
};

const source = fs.readFileSync(sourcePath, 'utf8');
const fftSize = readNumber(source, 'ANALYSER_FFT_SIZE');
const smoothing = readNumber(source, 'ANALYSER_SMOOTHING');
const minDb = readNumber(source, 'ANALYSER_MIN_DB');
const maxDb = readNumber(source, 'ANALYSER_MAX_DB');

// 整数也明确写成带小数点的浮点字面量，避免 `-100.f` 这种容易误读的形式。
const asFloat = value => (Number.isInteger(value) ? `${value.toFixed(1)}f` : `${value}f`);

const header = `// native/luma-audio/spectrum_contract.h
// GENERATED FILE - do not edit. Source: src/services/analyserContract.ts
// Regenerate with: npm run native:spectrum-contract
//
// 这四个值必须和网页 AnalyserNode 完全一致，否则共享模式与独占模式下的可视化会不一致。

#pragma once

// FFT 点数；frequencyBinCount 是它的一半。
constexpr unsigned SPECTRUM_FFT = ${fftSize}u;
constexpr unsigned SPECTRUM_BINS = SPECTRUM_FFT / 2;
// 帧间平滑系数。
constexpr float SPECTRUM_SMOOTHING = ${asFloat(smoothing)};
// getByteFrequencyData 的 dBFS 映射区间。
constexpr float SPECTRUM_MIN_DB = ${asFloat(minDb)};
constexpr float SPECTRUM_MAX_DB = ${asFloat(maxDb)};
`;

if (fs.existsSync(targetPath)) {
    const current = fs.readFileSync(targetPath, 'utf8');
    if (current === header) {
        console.log('[native] spectrum_contract.h is up to date');
        process.exit(0);
    }
}

fs.writeFileSync(targetPath, header, 'utf8');
console.log(`[native] wrote ${path.relative(root, targetPath)} (fft ${fftSize}, smoothing ${smoothing}, ${minDb}..${maxDb} dB)`);
