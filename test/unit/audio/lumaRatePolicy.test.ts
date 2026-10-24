// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';

// The store reads localStorage at import time; a plain Map stands in for it.
const storage = vi.hoisted(() => {
    const items = new Map<string, string>();
    Object.defineProperty(globalThis, 'localStorage', {
        configurable: true,
        value: {
            getItem: (key: string) => items.get(key) ?? null,
            setItem: (key: string, value: string) => { items.set(key, String(value)); },
            removeItem: (key: string) => { items.delete(key); },
            clear: () => items.clear(),
            key: (index: number) => Array.from(items.keys())[index] ?? null,
            get length() { return items.size; },
        },
    });
    return items;
});

const devices = vi.hoisted(() => ({
    list: [] as Array<{ backend: number; index: number; id: string; name: string; default: boolean }>,
}));

Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: {
        electron: {
            lumaAudio: {
                isAvailable: async () => true,
                getDevices: async () => ({ ok: true, devices: devices.list }),
            },
        },
    },
});

import { resolveLumaForceRate, useLumaAudioStore } from '@/stores/useLumaAudioStore';
import { mustWaitForNativeEngine } from '@/components/app/playback/bridges/nativeEngineStartGate';

// test/unit/audio/lumaRatePolicy.test.ts
// forceRate is what makes an exclusive/ASIO DAC lock to the configured rate:
// shared mode is resampled by the system mixer and the DSD passthrough paths
// carry their own fixed rate, so neither may forward pcmRate as forceRate.

const base = { pcmRate: 96000 };

describe('resolveLumaForceRate', () => {
    it('forwards pcmRate only for exclusive/ASIO PCM at rateMode 1', () => {
        expect(resolveLumaForceRate({ ...base, backend: 1, dsdMode: 0, rateMode: 1 })).toBe(96000);
        expect(resolveLumaForceRate({ ...base, backend: 2, dsdMode: 0, rateMode: 1 })).toBe(96000);
    });

    it('keeps shared mode, follow-source and non-PCM DSD paths bit-perfect', () => {
        expect(resolveLumaForceRate({ ...base, backend: 0, dsdMode: 0, rateMode: 1 })).toBe(0);
        expect(resolveLumaForceRate({ ...base, backend: 1, dsdMode: 0, rateMode: 0 })).toBe(0);
        expect(resolveLumaForceRate({ ...base, backend: 1, dsdMode: 1, rateMode: 1 })).toBe(0);
        expect(resolveLumaForceRate({ ...base, backend: 2, dsdMode: 2, rateMode: 1 })).toBe(0);
    });
});

describe('mustWaitForNativeEngine', () => {
    it('waits only when a bridge exists and the engine is switched on', () => {
        expect(mustWaitForNativeEngine(true, true)).toBe(true);
        expect(mustWaitForNativeEngine(true, false)).toBe(false);
        expect(mustWaitForNativeEngine(false, true)).toBe(false);
    });
});

describe('setBackend', () => {
    beforeEach(async () => {
        await useLumaAudioStore.getState().init();
    });

    it('switches to the default device of the new backend after refreshing', async () => {
        devices.list = [
            { backend: 1, index: 3, id: 'wasapi', name: 'WASAPI DAC', default: true },
            { backend: 2, index: 0, id: 'asio', name: 'ASIO DAC', default: true },
        ];
        await useLumaAudioStore.getState().setBackend(2);
        expect(useLumaAudioStore.getState()).toMatchObject({ backend: 2, activeDeviceId: 'asio', activeDeviceIndex: 0 });

        await useLumaAudioStore.getState().setBackend(1);
        expect(useLumaAudioStore.getState()).toMatchObject({ backend: 1, activeDeviceId: 'wasapi', activeDeviceIndex: 3 });
    });

    it('never publishes a new backend paired with the old backend\'s device index', async () => {
        // The reopen scheduler subscribes to this store. Writing backend first and the device
        // second used to let it open the new backend with the old index: on ASIO that is a
        // different driver slot, and the rate it then set belonged to the wrong device.
        devices.list = [
            { backend: 1, index: 7, id: 'wasapi-7', name: 'WASAPI DAC', default: true },
            { backend: 2, index: 1, id: 'asio-1', name: 'ASIO DAC', default: true },
        ];
        await useLumaAudioStore.getState().setBackend(1);

        const seen: Array<{ backend: number; index: number; }> = [];
        const unsubscribe = useLumaAudioStore.subscribe(state => {
            seen.push({ backend: state.backend, index: state.activeDeviceIndex });
        });
        await useLumaAudioStore.getState().setBackend(2);
        unsubscribe();

        expect(seen.length).toBeGreaterThan(0);
        seen.forEach(entry => expect(entry).toEqual({ backend: 2, index: 1 }));
    });
});
