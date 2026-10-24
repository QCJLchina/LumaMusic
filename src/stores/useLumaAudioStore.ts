// src/stores/useLumaAudioStore.ts
// Native high-performance audio engine state and profile management for LumaMusic v2.

import { create } from 'zustand';
import { getStoredBoolean, getStoredString, setStoredBoolean, setStoredString } from './storagePrimitives';

export const LUMA_ENGINE_ENABLED_KEY = 'luma_engine_enabled';
export const LUMA_BACKEND_KEY = 'luma_backend';
export const LUMA_DEVICE_ID_KEY = 'luma_device_id';
export const LUMA_DEVICE_INDEX_KEY = 'luma_device_index';
export const LUMA_DEVICE_NAME_KEY = 'luma_device_name';
export const LUMA_DSD_MODE_KEY = 'luma_dsd_mode';
export const LUMA_PCM_RATE_KEY = 'luma_pcm_rate';
export const LUMA_RATE_MODE_KEY = 'luma_rate_mode';

export interface LumaAudioStoreState {
    isAvailable: boolean;
    engineEnabled: boolean;
    backend: number; // 0: Shared, 1: Exclusive, 2: ASIO
    devices: LumaAudioDevice[];
    activeDeviceId: string;
    activeDeviceIndex: number;
    activeDeviceName: string;
    dsdMode: number; // 0: PCM, 1: DoP, 2: Native DSD
    pcmRate: number;
    rateMode: number; // 0: follow source, 1: resample to pcmRate
    forceRate: number;
    downmix: boolean;
    mapping: number[];
    playbackState: Partial<LumaPlaybackState>;
    isLumaPlaying: boolean;
    openError: string;
    /** True while the native engine holds the output: the web audio path then only feeds visuals. */
    engineOwnsOutput: boolean;

    init: () => Promise<void>;
    refreshDevices: () => Promise<LumaAudioDevice[]>;
    setEngineEnabled: (enabled: boolean) => void;
    setBackend: (backend: number) => Promise<void>;
    setDevice: (device: LumaAudioDevice) => void;
    setDsdMode: (mode: number) => void;
    setPcmRate: (rate: number) => void;
    setRateMode: (mode: number) => void;
    updatePlaybackState: (state: Partial<LumaPlaybackState>) => void;
}

// 重采样只在独占/ASIO 的 PCM 路径有意义：共享模式由系统混音器接管，
// DSD 透传/DoP 有各自固定的率，都不能被 forceRate 覆盖。
export const resolveLumaForceRate = (state: Pick<LumaAudioStoreState, 'backend' | 'dsdMode' | 'pcmRate' | 'rateMode'>): number =>
    state.backend !== 0 && state.rateMode === 1 && state.dsdMode === 0 ? state.pcmRate : 0;

const getStoredNumber = (key: string, fallback: number): number => {
    if (typeof window === 'undefined') return fallback;
    const val = localStorage.getItem(key);
    const parsed = val !== null ? parseInt(val, 10) : NaN;
    return Number.isFinite(parsed) ? parsed : fallback;
};

const setStoredNumber = (key: string, val: number) => {
    if (typeof window !== 'undefined') {
        localStorage.setItem(key, String(val));
    }
};

export const useLumaAudioStore = create<LumaAudioStoreState>((set, get) => ({
    isAvailable: false,
    engineEnabled: getStoredBoolean(LUMA_ENGINE_ENABLED_KEY, true),
    backend: getStoredNumber(LUMA_BACKEND_KEY, 1), // Default to WASAPI Exclusive
    devices: [],
    activeDeviceId: getStoredString(LUMA_DEVICE_ID_KEY, ''),
    activeDeviceIndex: getStoredNumber(LUMA_DEVICE_INDEX_KEY, -1),
    activeDeviceName: getStoredString(LUMA_DEVICE_NAME_KEY, ''),
    dsdMode: getStoredNumber(LUMA_DSD_MODE_KEY, 0), // 0: PCM
    pcmRate: getStoredNumber(LUMA_PCM_RATE_KEY, 176400),
    rateMode: getStoredNumber(LUMA_RATE_MODE_KEY, 1),
    forceRate: 0,
    downmix: false,
    mapping: [0, 1, 2, 3, 4, 5, 6, 7],
    playbackState: {},
    isLumaPlaying: false,
    openError: '',
    engineOwnsOutput: false,

    init: async () => {
        if (typeof window === 'undefined' || !window.electron?.lumaAudio) {
            set({ isAvailable: false });
            return;
        }
        try {
            const available = await window.electron.lumaAudio.isAvailable();
            if (!available) {
                set({ isAvailable: false });
                return;
            }
            set({ isAvailable: true });
            await get().refreshDevices();
        } catch {
            set({ isAvailable: false });
        }
    },

    refreshDevices: async () => {
        if (typeof window === 'undefined' || !window.electron?.lumaAudio) return [];
        try {
            const res = await window.electron.lumaAudio.getDevices();
            if (res.ok && Array.isArray(res.devices)) {
                set({ devices: res.devices });
                // 索引只在同一套枚举里唯一：WASAPI 的 1 和 ASIO 的 1 是两台不同的设备。
                const currentDevice = res.devices.find(d => d.backend === get().backend && d.id === get().activeDeviceId)
                    ?? res.devices.find(d => d.backend === get().backend && d.index === get().activeDeviceIndex);
                if (!currentDevice && res.devices.length > 0) {
                    const defaultDev = res.devices.find(d => d.backend === get().backend && d.default) ||
                                       res.devices.find(d => d.backend === get().backend) ||
                                       res.devices[0];
                    if (defaultDev) {
                        get().setDevice(defaultDev);
                    }
                }
                return res.devices;
            }
        } catch {}
        return [];
    },

    setEngineEnabled: (enabled: boolean) => {
        setStoredBoolean(LUMA_ENGINE_ENABLED_KEY, enabled);
        set({ engineEnabled: enabled });
    },

    setBackend: async (backend: number) => {
        // 设备索引在 WASAPI 与 ASIO 两套枚举间不通用。先同步拿到新后端的设备，
        // 再一次性写进 store：分两步写会让"重开引擎"的订阅在设备还没换好时
        // 就用旧索引去开新后端，切后端后要么开错设备、要么直接失败。
        const res = await window.electron?.lumaAudio?.getDevices().catch(() => null);
        const list = res?.ok && Array.isArray(res.devices) ? res.devices : get().devices;
        const device = list.find(d => d.backend === backend && d.default)
            || list.find(d => d.backend === backend);

        setStoredNumber(LUMA_BACKEND_KEY, backend);
        if (!device) {
            set({ backend, ...(res?.ok && Array.isArray(res.devices) ? { devices: res.devices } : {}) });
            return;
        }
        setStoredString(LUMA_DEVICE_ID_KEY, device.id);
        setStoredNumber(LUMA_DEVICE_INDEX_KEY, device.index);
        setStoredString(LUMA_DEVICE_NAME_KEY, device.name);
        set({
            backend,
            ...(list !== get().devices ? { devices: list } : {}),
            activeDeviceId: device.id,
            activeDeviceIndex: device.index,
            activeDeviceName: device.name,
            playbackState: {},
        });
    },

    setDevice: (device: LumaAudioDevice) => {
        setStoredString(LUMA_DEVICE_ID_KEY, device.id);
        setStoredNumber(LUMA_DEVICE_INDEX_KEY, device.index);
        setStoredString(LUMA_DEVICE_NAME_KEY, device.name);
        setStoredNumber(LUMA_BACKEND_KEY, device.backend);
        set({
            activeDeviceId: device.id,
            activeDeviceIndex: device.index,
            activeDeviceName: device.name,
            backend: device.backend,
            // 设备一换，上一台设备的实际输出参数就不作数了；等新引擎报上来再显示，
            // 否则界面会拿旧设备的率/位深冒充新设备。
            playbackState: {},
        });
    },

    setDsdMode: (mode: number) => {
        setStoredNumber(LUMA_DSD_MODE_KEY, mode);
        set({ dsdMode: mode });
    },

    setPcmRate: (rate: number) => {
        setStoredNumber(LUMA_PCM_RATE_KEY, rate);
        set({ pcmRate: rate });
    },

    setRateMode: (mode: number) => {
        setStoredNumber(LUMA_RATE_MODE_KEY, mode);
        set({ rateMode: mode });
    },

    updatePlaybackState: (playbackState: Partial<LumaPlaybackState>) => {
        set({
            playbackState,
            isLumaPlaying: Boolean(playbackState.playing),
            engineOwnsOutput: playbackState.failed ? false : (playbackState.playing ?? false) || get().engineOwnsOutput,
        });
    },
}));
