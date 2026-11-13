// src/services/webOutputOwnership.ts
// 「谁在占用输出设备」的唯一所有者：网页这一路的输出门、AudioContext 的挂起/恢复、
// 以及「本会话是否已经放弃释放设备」都只在这里决定。
//
// 放在 services 而不是 components：它持有跨模块的可变状态（当前图表、放弃标志、看门狗计数），
// 和 services/playbackFade.ts 性质相同。组件层与 hooks 层都依赖它，反向则不允许。
//
// 没有它的时候这套规则分散在音频桥、传输控制器和输出设备选择里，各自读 `engineOwnsOutput`
// 或 `context.state`，任何一处漏改都会让独占悄悄退化成共享、或者让网页这一路永久静音。
// 其余模块只调用下面的函数，不再自己判断 context 状态。
//
// 一个持续存在的限制写在这里，免得后来者以为是 bug：Chromium 里媒体元素的时钟由图表供给，
// 图表被挂起时元素的 `currentTime` 可能不再推进，而那会带走 `timeupdate` / `ended` / automix。
// 所以 releaseWebGraphToEngine() 之后必须有 superviseWebGraph() 看着，见它的注释。

import { useLumaAudioStore } from '../stores/useLumaAudioStore';

type WebGraph = { context: AudioContext; gate: GainNode };

/** 引擎接手后，deck 时钟多久没动就判定「挂起把队列冻住了」，单位毫秒。 */
const DEVICE_RELEASE_FROZEN_MS = 1000;

/** 判定两个时刻「一样」的阈值，秒。50ms 轮询下移动 1e-4 秒就算在走。 */
const CLOCK_EPSILON_SEC = 1e-4;

let graph: WebGraph | null = null;
/** 本会话是否已经放弃「挂起图表以释放设备」这条路。 */
let deviceReleaseAbandoned = false;
/** 网页这一路的输出门当前是否是关闭的（也就是引擎是否正占着输出）。 */
let outputSilenced = false;
/** 正在挂起中，避免同一时刻连发几次 suspend()。 */
let suspending = false;
/** 看门狗状态：上一次看到的 deck 时间，以及连续多少次没推进。 */
let lastDeckTime = -1;
let frozenPolls = 0;

/** 由音频桥在播放图建好时登记。这是 `graph` 的唯一写入点。 */
export const registerWebOutputGraph = (next: WebGraph | null) => {
    graph = next;
    lastDeckTime = -1;
    frozenPolls = 0;
};

/**
 * 设备是否确实交给了引擎。
 *
 * `outputSilenced` 是「输出归引擎」的唯一状态来源；context 是否挂起只是它的释放结果。
 * 两个条件一起判断，避免「输出门和 context 各自给出不同答案」。
 */
export const isWebOutputReleased = (): boolean =>
    outputSilenced && graph !== null && graph.context.state === 'suspended';

/** 让图表恢复运行；调用方自行决定输出门是否仍保持关闭。 */
export const resumeWebGraph = async () => {
    const current = graph;
    if (!current) return;
    try {
        if (current.context.state === 'suspended') await current.context.resume();
    } catch (error) {
        // 图表起不来不等于不能出声：引擎那一路照常，网页这一路走它自己的 sink。
        console.warn('[LumaAudio] audio context did not resume', error);
    }
};

/**
 * 关掉网页这一路的输出门。
 *
 * 只关增益、不改变 context 状态：引擎接手时这一步先做，真正的设备释放交给轮询里的
 * releaseWebGraphToEngine()——在下一次 play() 之前挂起图表会让那个 Promise 悬住。
 */
export const silenceWebOutput = () => {
    outputSilenced = true;
    const current = graph;
    if (!current) return;
    current.gate.gain.cancelScheduledValues(current.context.currentTime);
    current.gate.gain.setValueAtTime(0, current.context.currentTime);
};

/** 交还输出：打开输出门并让图表重新跑起来，网页这一路才能出声。 */
export const handOutputBackToWeb = async () => {
    outputSilenced = false;
    const current = graph;
    if (!current) return;
    current.gate.gain.cancelScheduledValues(current.context.currentTime);
    current.gate.gain.setValueAtTime(1, current.context.currentTime);
    await resumeWebGraph();
};

/**
 * 引擎在播放途中失败时，把输出交还给网页这一路。
 *
 * 引擎失败（拔掉 USB DAC、WASAPI 设备重置、解码中断）会让 store 里的 `engineOwnsOutput`
 * 变成 false，但那只翻了一个字段——输出门仍被钉在 0，网页这一路依然不出声，用户听到的是
 * 彻底无声。真正需要解除的是这里记着的 `outputSilenced`，所以交还动作必须由本模块完成，
 * 不能靠别处读 store 字段。
 *
 * @returns 之前确实静音着、这次真的交还了
 */
export const releaseOutputAfterEngineFailure = async (): Promise<boolean> => {
    if (!outputSilenced) return false;
    await handOutputBackToWeb();
    return true;
};

/**
 * 挂起图表，把设备真正交给引擎。
 *
 * 共享模式下引擎与系统混音器共用设备，没有可释放的东西，关掉输出门就够，所以这里直接跳过。
 */
const releaseWebGraphToEngine = async () => {
    const current = graph;
    if (!current || deviceReleaseAbandoned) return;
    if (useLumaAudioStore.getState().backend === 0) return;
    if (current.context.state === 'suspended') return;
    try {
        await current.context.suspend();
        console.log('[LumaAudio] web audio graph suspended: the engine now holds the output device alone');
    } catch (error) {
        // 释放失败不影响播放：输出门仍然关着，只是设备被共享。
        deviceReleaseAbandoned = true;
        console.warn('[LumaAudio] could not suspend the web audio graph; falling back to the closed output gate', error);
    }
};

/**
 * 每个状态轮询调用一次，驱动设备释放并看住 deck 时钟。
 *
 * 挂起图表会释放设备，但也可能让 deck 的 `currentTime` 停住——那会带走 `timeupdate`、
 * `ended`、automix 过渡点和听歌上报，队列就再也不前进。所以先确认 deck 真的在走才挂起；
 * 挂起之后若它是停的，就放弃释放（图表恢复、输出门保持关闭），退回「引擎出声 + 网页静音」
 * 的旧行为。放弃之后本会话不再重试，否则每首歌都会冻一次。
 *
 * @returns 本次是否刚刚放弃了设备释放（调用方据此提示一次）
 */
export const superviseWebGraph = (deck: HTMLAudioElement | null, pollIntervalMs: number): boolean => {
    if (deviceReleaseAbandoned) return false;
    const current = graph;
    if (!current || !deck) return false;
    // 输出已经不归引擎了（引擎失败或正常停止），没有可释放的东西，也无需再看门。
    if (!outputSilenced) {
        lastDeckTime = -1;
        frozenPolls = 0;
        return false;
    }
    // deck 被暂停或在曲间停驻时没有可观察的时钟，也不算冻结。
    if (deck.paused || deck.ended) {
        lastDeckTime = -1;
        frozenPolls = 0;
        return false;
    }
    if (current.context.state === 'running') {
        const moved = lastDeckTime >= 0 && Math.abs(deck.currentTime - lastDeckTime) > CLOCK_EPSILON_SEC;
        if (moved && !suspending) {
            frozenPolls = 0;
            suspending = true;
            void releaseWebGraphToEngine().finally(() => { suspending = false; });
        }
        lastDeckTime = deck.currentTime;
        return false;
    }
    if (current.context.state !== 'suspended') {
        // `closed`（图表已被销毁）与 `interrupted` 都落在这里：都不是「挂起把时钟冻住了」，
        // 所以计数必须归零，否则恢复之后会拿上一轮累加的值立刻误判成冻结。
        lastDeckTime = -1;
        frozenPolls = 0;
        return false;
    }
    frozenPolls = Math.abs(deck.currentTime - lastDeckTime) < CLOCK_EPSILON_SEC ? frozenPolls + 1 : 0;
    lastDeckTime = deck.currentTime;
    const threshold = Math.max(1, Math.ceil(DEVICE_RELEASE_FROZEN_MS / Math.max(1, pollIntervalMs)));
    if (frozenPolls < threshold) return false;
    deviceReleaseAbandoned = true;
    void resumeWebGraph();
    console.warn('[LumaAudio] web deck clock stopped while the audio context was suspended; '
        + 'resuming it and keeping the output gate closed instead of a real device release');
    return true;
};
