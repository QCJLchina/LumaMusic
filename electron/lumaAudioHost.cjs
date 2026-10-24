const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');
const http = require('http');
const https = require('https');

// electron/lumaAudioHost.cjs
// Native high-performance audio engine bridge for LumaMusic v2 (C++ / BASS / ASIO / DSD).
// Loads LumaAudio.dll through koffi in the Electron main process.

let bound = null;
let initialized = false;


function resolveAudioBinDir() {
    const candidates = [
        // Packaged layout: extraResources puts audio-bin under the real resources dir.
        // The copies under electron/ land inside app.asar, which LoadLibrary cannot read.
        process.resourcesPath ? path.join(process.resourcesPath, 'audio-bin', 'win-x64') : null,
        path.join(__dirname, 'resources', 'audio-bin', 'win-x64'),
        path.join(__dirname, '..', 'electron', 'resources', 'audio-bin', 'win-x64'),
        'D:\\soft\\lumamusic\\build\\native-bin',
    ].filter(Boolean);
    for (const dir of candidates) {
        if (fs.existsSync(path.join(dir, 'LumaAudio.dll'))) {
            return dir;
        }
    }
    return null;
}

function ensureLoaded() {
    if (bound) return bound;
    if (process.platform !== 'win32') {
        throw new Error('LumaAudio native engine is currently supported on Windows only.');
    }

    const binDir = resolveAudioBinDir();
    if (!binDir) {
        throw new Error('LumaAudio native binaries not found. Ensure LumaAudio.dll is deployed.');
    }

    // Add binDir to PATH so Windows DLL loader resolves bass.dll, bassasio.dll, etc.
    if (!process.env.PATH.includes(binDir)) {
        process.env.PATH = `${binDir};${process.env.PATH}`;
    }

    const koffi = require('koffi');
    const dllPath = path.join(binDir, 'LumaAudio.dll');
    const lib = koffi.load(dllPath);

    bound = {
        binDir,
        luma_init: lib.func('int luma_init(const char16_t* directory)'),
        luma_devices: lib.func('const char* luma_devices()'),
        luma_error: lib.func('const char* luma_error()'),
        luma_state: lib.func('const char* luma_state()'),
        luma_open: lib.func('int luma_open(const char16_t* file, int track, int backend, int device, int mode, int pcmRate, int forceRate, int downmix, const int* map, float volume, double position, double limit, float gainLinear, double cueStart, double fadeIn)'),
        luma_pause: lib.func('int luma_pause(int pause)'),
        luma_fade: lib.func('int luma_fade(float target, double seconds)'),
        luma_fade_wait: lib.func('int luma_fade_wait(float target, double seconds)'),
        luma_seek: lib.func('int luma_seek(double position)'),
        luma_stop: lib.func('void luma_stop()'),
        luma_volume: lib.func('void luma_volume(float v)'),
        luma_shutdown: lib.func('void luma_shutdown()'),
    };

    const ok = bound.luma_init(binDir);
    if (!ok) {
        const err = bound.luma_error() || 'Initialization failed';
        bound = null;
        throw new Error(`Failed to initialize LumaAudio engine: ${err}`);
    }
    initialized = true;
    console.log('[LumaAudioHost] Native engine initialized from', binDir);
    return bound;
}

const STREAM_DIR = path.join(os.tmpdir(), 'lumamusic-luma-stream');
const STREAM_CACHE_LIMIT_BYTES = 2 * 1024 * 1024 * 1024;
// 正在写的下载：同一地址的并发请求共用一次下载，不同地址互不影响。
const pendingStreams = new Map();

/** Records a staged file so the LRU sweep knows it is in use. */
function touchStagedFile(file) {
    try { const now = new Date(); fs.utimesSync(file, now, now); } catch {}
}

/** Drops the oldest stream files once the cache directory is over its budget. */
function pruneStreamCache() {
    try {
        const entries = fs.readdirSync(STREAM_DIR)
            .map(name => path.join(STREAM_DIR, name))
            .map(file => ({ file, stat: fs.statSync(file) }))
            .sort((a, b) => a.stat.mtimeMs - b.stat.mtimeMs);
        let total = entries.reduce((sum, entry) => sum + entry.stat.size, 0);
        for (const entry of entries) {
            if (total <= STREAM_CACHE_LIMIT_BYTES) break;
            try { fs.unlinkSync(entry.file); total -= entry.stat.size; } catch {}
        }
    } catch {}
}

/**
 * Downloads a remote audio URL to a cache file so BASS can decode it.
 *
 * Keyed on the address, but a hit is only trusted while the file is young enough:
 * platform CDN links expire, and a stale one decodes as garbage rather than failing loudly.
 */
function downloadRemoteAudioToTemp(url, headers = null) {
    const hash = crypto.createHash('md5').update(url).digest('hex');
    const tempPath = path.join(STREAM_DIR, `luma_stream_${hash}.audio`);

    if (fs.existsSync(tempPath)) {
        const ageMs = Date.now() - fs.statSync(tempPath).mtimeMs;
        if (fs.statSync(tempPath).size > 0 && ageMs < 10 * 60 * 1000) {
            touchStagedFile(tempPath);
            return Promise.resolve(tempPath);
        }
        try { fs.unlinkSync(tempPath); } catch {}
    }

    if (pendingStreams.has(url)) return pendingStreams.get(url);

    const download = new Promise((resolve, reject) => {
        fs.mkdirSync(STREAM_DIR, { recursive: true });
        const partial = `${tempPath}.${process.pid}.part`;
        const fileStream = fs.createWriteStream(partial);
        const client = url.startsWith('https:') ? https : http;
        const request = client.get(url, { headers: { 'User-Agent': 'Mozilla/5.0', ...(headers || {}) } }, (res) => {
            if (res.statusCode && res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
                fileStream.close();
                try { fs.unlinkSync(partial); } catch {}
                resolve(downloadRemoteAudioToTemp(new URL(res.headers.location, url).toString(), headers));
                return;
            }
            if (res.statusCode !== 200) {
                fileStream.close();
                try { fs.unlinkSync(partial); } catch {}
                reject(new Error(`Failed to stream online audio: HTTP ${res.statusCode}`));
                return;
            }
            res.pipe(fileStream);
            fileStream.on('finish', () => {
                fileStream.close(() => {
                    try {
                        fs.renameSync(partial, tempPath);
                        pruneStreamCache();
                        resolve(tempPath);
                    } catch (error) {
                        reject(error);
                    }
                });
            });
        });
        request.on('error', (error) => {
            fileStream.close();
            try { fs.unlinkSync(partial); } catch {}
            reject(error);
        });
    }).finally(() => pendingStreams.delete(url));

    pendingStreams.set(url, download);
    return download;
}

const lumaAudioHost = {
    isAvailable() {
        return process.platform === 'win32';
    },

    init() {
        try {
            ensureLoaded();
            return { ok: true };
        } catch (error) {
            console.error('[LumaAudioHost] Init failed:', error);
            return { ok: false, error: error.message };
        }
    },

    getDevices() {
        try {
            const api = ensureLoaded();
            const raw = api.luma_devices();
            const list = JSON.parse(raw || '[]');
            return { ok: true, devices: list };
        } catch (error) {
            return { ok: false, error: error.message, devices: [] };
        }
    },

    async open(params) {
        try {
            const api = ensureLoaded();
            let filePath = params.file;
            if (filePath.startsWith('http://') || filePath.startsWith('https://')) {
                filePath = await downloadRemoteAudioToTemp(filePath, params.headers);
            }

            const track = params.track || 1;
            const backend = params.backend !== undefined ? params.backend : 1; // 1: WASAPI exclusive by default
            const device = params.device !== undefined ? params.device : -1;
            const mode = params.mode !== undefined ? params.mode : 0; // 0: PCM, 1: DoP, 2: Native DSD
            const pcmRate = params.pcmRate || 176400;
            const forceRate = params.forceRate || 0;
            const downmix = params.downmix ? 1 : 0;
            const map = params.map && params.map.length === 8 ? params.map : null;
            const volume = params.volume !== undefined ? params.volume : 0.8;
            const position = params.position || 0.0;
            const limit = params.limit || 0.0;
            const gainLinear = params.gainLinear || 1.0;
            const cueStart = params.cueStart || 0.0;
            const fadeIn = params.fadeIn !== undefined ? params.fadeIn : 0.0;

            const ok = api.luma_open(
                filePath, track, backend, device, mode,
                pcmRate, forceRate, downmix, map,
                volume, position, limit, gainLinear, cueStart, fadeIn
            );

            if (!ok) {
                const err = api.luma_error() || 'Failed to open audio';
                console.warn('[LumaAudio] open failed:', err);
                return { ok: false, error: err };
            }
            const state = JSON.parse(api.luma_state() || '{}');
            console.log('[LumaAudio] opened:', {
                backend: state.backend,
                rate: state.rate,
                channels: state.channels,
                deviceBits: state.deviceBits,
            });
            return { ok: true };
        } catch (error) {
            return { ok: false, error: error.message };
        }
    },

    pause(pauseVal) {
        try {
            const api = ensureLoaded();
            const ok = api.luma_pause(pauseVal ? 1 : 0);
            return { ok: Boolean(ok) };
        } catch (error) {
            return { ok: false, error: error.message };
        }
    },

    fade(target, seconds) {
        try {
            const api = ensureLoaded();
            return { ok: Boolean(api.luma_fade(Number(target) || 0, Number(seconds) || 0)) };
        } catch (error) {
            return { ok: false, error: error.message };
        }
    },

    fadeWait(target, seconds) {
        try {
            const api = ensureLoaded();
            return { ok: Boolean(api.luma_fade_wait(Number(target) || 0, Number(seconds) || 0)) };
        } catch (error) {
            return { ok: false, error: error.message };
        }
    },

    seek(position) {
        try {
            const api = ensureLoaded();
            const ok = api.luma_seek(Number(position) || 0);
            return { ok: Boolean(ok) };
        } catch (error) {
            return { ok: false, error: error.message };
        }
    },

    stop() {
        try {
            const api = ensureLoaded();
            api.luma_stop();
            return { ok: true };
        } catch (error) {
            return { ok: false, error: error.message };
        }
    },

    setVolume(volume) {
        try {
            const api = ensureLoaded();
            api.luma_volume(Math.max(0, Math.min(1, Number(volume) || 0)));
            return { ok: true };
        } catch (error) {
            return { ok: false, error: error.message };
        }
    },

    getState() {
        try {
            const api = ensureLoaded();
            const raw = api.luma_state();
            const state = JSON.parse(raw || '{}');
            return { ok: true, state };
        } catch (error) {
            return { ok: false, error: error.message, state: {} };
        }
    },

    shutdown() {
        if (bound && initialized) {
            try {
                bound.luma_shutdown();
            } catch {}
            initialized = false;
        }
    },

    /** Keeps a staged file out of the LRU sweep while it is still being played. */
    touchStagedFile,
};

module.exports = lumaAudioHost;
