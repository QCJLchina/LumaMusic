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
let activeTempStreamFile = null;

function resolveAudioBinDir() {
    const candidates = [
        path.join(__dirname, 'resources', 'audio-bin', 'win-x64'),
        path.join(__dirname, '..', 'electron', 'resources', 'audio-bin', 'win-x64'),
        'D:\\soft\\lumamusic\\build\\native-bin',
    ];
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
        luma_open: lib.func('int luma_open(const char16_t* file, int track, int backend, int device, int mode, int pcmRate, int forceRate, int downmix, const int* map, float volume, double position, double limit, float gainLinear, double cueStart)'),
        luma_pause: lib.func('int luma_pause(int pause)'),
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

/**
 * Downloads a remote audio URL to a local temporary file so BASS can decode it.
 */
async function downloadRemoteAudioToTemp(url) {
    if (activeTempStreamFile && fs.existsSync(activeTempStreamFile)) {
        try { fs.unlinkSync(activeTempStreamFile); } catch {}
    }

    const hash = crypto.createHash('md5').update(url).digest('hex');
    const tempPath = path.join(os.tmpdir(), `luma_stream_${hash}.tmp`);
    activeTempStreamFile = tempPath;

    if (fs.existsSync(tempPath) && fs.statSync(tempPath).size > 0) {
        return tempPath;
    }

    return new Promise((resolve, reject) => {
        const fileStream = fs.createWriteStream(tempPath);
        const client = url.startsWith('https:') ? https : http;
        const req = client.get(url, { headers: { 'User-Agent': 'Mozilla/5.0' } }, (res) => {
            if (res.statusCode && res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
                fileStream.close();
                downloadRemoteAudioToTemp(res.headers.location).then(resolve).catch(reject);
                return;
            }
            if (res.statusCode !== 200) {
                fileStream.close();
                reject(new Error(`Failed to stream online audio: HTTP ${res.statusCode}`));
                return;
            }
            res.pipe(fileStream);
            fileStream.on('finish', () => {
                fileStream.close();
                resolve(tempPath);
            });
        });
        req.on('error', (err) => {
            try { fs.unlinkSync(tempPath); } catch {}
            reject(err);
        });
    });
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
                filePath = await downloadRemoteAudioToTemp(filePath);
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

            const ok = api.luma_open(
                filePath, track, backend, device, mode,
                pcmRate, forceRate, downmix, map,
                volume, position, limit, gainLinear, cueStart
            );

            if (!ok) {
                const err = api.luma_error() || 'Failed to open audio';
                return { ok: false, error: err };
            }
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
    }
};

module.exports = lumaAudioHost;
