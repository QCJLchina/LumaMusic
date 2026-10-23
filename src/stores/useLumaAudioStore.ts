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
    forceRate: number;
    downmix: boolean;
    mapping: number[];
    playbackState: Partial<LumaPlaybackState>;
    isLumaPlaying: boolean;

    init: () => Promise<void>;
    refreshDevices: () => Promise<LumaAudioDevice[]>;
    setEngineEnabled: (enabled: boolean) => void;
    setBackend: (backend: number) => void;
    setDevice: (device: LumaAudioDevice) => void;
    setDsdMode: (mode: number) => void;
    setPcmRate: (rate: number) => void;
    updatePlaybackState: (state: Partial<LumaPlaybackState>) => void;
}

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
    forceRate: 0,
    downmix: false,
    mapping: [0, 1, 2, 3, 4, 5, 6, 7],
    playbackState: {},
    isLumaPlaying: false,

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
                const currentDevice = res.devices.find(d =>
                    d.backend === get().backend && (d.id === get().activeDeviceId || d.index === get().activeDeviceIndex)
                );
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

    setBackend: (backend: number) => {
        setStoredNumber(LUMA_BACKEND_KEY, backend);
        set({ backend });
        // Pick suitable device for this backend
        const dev = get().devices.find(d => d.backend === backend && (d.default || true));
        if (dev) {
            get().setDevice(dev);
        }
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

    updatePlaybackState: (playbackState: Partial<LumaPlaybackState>) => {
        set({
            playbackState,
            isLumaPlaying: Boolean(playbackState.playing),
        });
    },
}));
