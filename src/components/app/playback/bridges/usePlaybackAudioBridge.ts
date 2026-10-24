import { useCallback, useEffect, useMemo, useRef } from 'react';
import type { MutableRefObject, RefObject } from 'react';
import { PlayerState } from '../../../../types';
import type { ReplayGainInfo, ReplayGainMode, SongResult, StatusMessage } from '../../../../types';
import type { LocalSong } from '../../../../types';
import { resolveNavidromePlaybackCarrier } from '../../../../utils/appPlaybackGuards';
import { calculateReplayGain } from '../../../../utils/replayGain';
import { applyAudioEqualizerSettings, planAudioEqualizerApply } from '../../../../services/audioEqualizerGraph';
import { type AudioEffectChain } from '../../../../services/audioEffects/effectChain';
import { buildPlaybackGraph } from '../../../../services/playbackGraph';
import { cachePlayedTrackAssets } from '../../../../services/playedTrackCache';
import { registerPlaybackFadeGraph } from '../../../../services/playbackFade';
import { rampGain, type AutomixDeckChain } from '../../../../services/automix/crossfadeGraph';
import { setStatusMessage as setStatusMsg } from '../../../../stores/useStatusMessageStore';
import { useAudioSettingsStore } from '../../../../stores/useAudioSettingsStore';
import { setPlayerState } from '../../../../stores/usePlaybackStore';
import { useTranslation } from 'react-i18next';
import { usePlaybackStore } from '../../../../stores/usePlaybackStore';
import { resolveHeardAudioEqualizerSettings } from '../../../../utils/audioEqualizerModCurve';
import { resolveLumaForceRate, useLumaAudioStore } from '../../../../stores/useLumaAudioStore';
import { currentTime } from '../../../../stores/motionSignals';
import { PLAYBACK_FADE_SECONDS } from '../../../../services/playbackFade';
import { mustWaitForNativeEngine } from './nativeEngineStartGate';

// src/components/app/playback/bridges/usePlaybackAudioBridge.ts

type UsePlaybackAudioBridgeParams = {

    audioRef: RefObject<HTMLAudioElement | null>;
    localSongs: LocalSong[];
    isLyricsLoading: boolean;
    shouldAutoPlayRef: MutableRefObject<boolean>;
    audioContextRef: MutableRefObject<AudioContext | null>;
    analyserRef: MutableRefObject<AnalyserNode | null>;
    gainNodeRef: MutableRefObject<GainNode | null>;
    /** Wires both automix decks into the shared mix point. Returns false if a deck is missing. */
    connectDecks: (context: AudioContext, output: AudioNode) => boolean;
    /** The deck the app is currently listening through, for per-track loudness compensation. */
    getActiveChain: () => AutomixDeckChain | null;
    /** Set when a pause interrupted an armed transition; suppresses exactly one autoplay. */
    suppressAutoplayRef: MutableRefObject<boolean>;
    /** True while automix has the next track loaded but its blend is not due to start yet. */
    isAutoplayHeld: boolean;
    syncOutputGain: (targetVolume: number, smoothing?: number) => void;
    getTargetPlaybackVolume: () => number;
    getCoverUrl: () => string | null;
    /** Whether an automix blend can be heard now; a mod's equalizer change waits until it cannot. */
    isTransitionAudible?: () => boolean;
};

/** How often a mod's equalizer change held back for a blend checks whether the blend has ended. */
const DEFERRED_EQUALIZER_POLL_MS = 250;

// Bridges audio element setup, autoplay, replay gain, and media caching.
export function usePlaybackAudioBridge({
    audioRef,
    localSongs,
    isLyricsLoading,
    shouldAutoPlayRef,
    audioContextRef,
    analyserRef,
    gainNodeRef,
    connectDecks,
    getActiveChain,
    suppressAutoplayRef,
    isAutoplayHeld,
    syncOutputGain,
    getTargetPlaybackVolume,
    getCoverUrl,
    isTransitionAudible,
}: UsePlaybackAudioBridgeParams) {
    // Read here rather than passed in: all store fields or i18n.
    const { t } = useTranslation();
    const audioSrc = usePlaybackStore(state => state.audioSrc);
    const currentSong = usePlaybackStore(state => state.currentSong);
    const replayGainMode = usePlaybackStore(state => state.replayGainMode);
    const enableMediaCache = useAudioSettingsStore(state => state.enableMediaCache);

    const previousAudioSrcRef = useRef<string | null>(null);
    const replayGainLogSignatureRef = useRef<string | null>(null);
    const equalizerNodesRef = useRef<BiquadFilterNode[]>([]);
    const effectChainRef = useRef<AudioEffectChain | null>(null);
    const outputGateRef = useRef<GainNode | null>(null);
    // The engine's own position and when it was sampled; used to advance the UI clock between
    // status polls instead of letting it stutter by one IPC round trip.
    const engineClockRef = useRef<{ position: number; at: number; sourceRate: number; } | null>(null);
    const audioEqualizerSettings = useAudioSettingsStore(state => state.audioEqualizerSettings);
    // What is heard: the listener's own settings with a mod's curve over the gains (Folium 1.7). Worked out
    // here rather than in a selector, which would hand useSyncExternalStore a new object on every read.
    const modCurve = useAudioSettingsStore(state => state.audioEqualizerModCurve);
    const modCurveSuspended = useAudioSettingsStore(state => state.audioEqualizerModCurveSuspended);
    const equalizerOrigin = useAudioSettingsStore(state => state.audioEqualizerChangeOrigin);
    const heardEqualizerSettings = useMemo(
        () => resolveHeardAudioEqualizerSettings(audioEqualizerSettings, modCurve, modCurveSuspended),
        [audioEqualizerSettings, modCurve, modCurveSuspended],
    );
    const heardEqualizerSettingsRef = useRef(heardEqualizerSettings);
    const isTransitionAudibleRef = useRef(isTransitionAudible);
    // Read only in the equalizer effect and its timer; written in an effect, not while rendering.
    useEffect(() => {
        isTransitionAudibleRef.current = isTransitionAudible;
    }, [isTransitionAudible]);

    // Recalculates source-specific gain after the audio graph or playback settings become ready.
    const applyReplayGain = useCallback(() => {
        if (!currentSong || !gainNodeRef.current || !audioContextRef.current) return;

        let replayGainInfo: ReplayGainInfo | undefined;
        const localSongId = (currentSong as SongResult & { localRef?: { songId: string } }).localRef?.songId;
        const localData = localSongId ? localSongs.find(song => song.id === localSongId) : undefined;
        if ((currentSong as SongResult & { isLocal?: boolean }).isLocal && localData) {
            replayGainInfo = {
                trackGain: typeof localData.replayGainTrackGain === 'number'
                    ? localData.replayGainTrackGain
                    : localData.replayGain,
                trackPeak: localData.replayGainTrackPeak,
                albumGain: localData.replayGainAlbumGain,
                albumPeak: localData.replayGainAlbumPeak,
            };
        }

        const navidromeSong = resolveNavidromePlaybackCarrier(currentSong);
        const navidromeReplayGain = navidromeSong?.navidromeData.replayGain;
        if (navidromeReplayGain) {
            replayGainInfo = navidromeReplayGain;
        }

        if (currentSong.sourceRef?.kind === 'online') {
            replayGainInfo = currentSong.replayGain;
        }

        const calculation = calculateReplayGain(replayGainInfo, replayGainMode);

        try {
            // Onto the deck rather than the shared output: loudness compensation is a per-track
            // value, so during a blend the two decks legitimately need different ones.
            const activeChain = getActiveChain();
            if (activeChain) {
                rampGain(audioContextRef.current, activeChain.replayGain, calculation.linearGain, 0.1);
            }

            const replayGainLogSignature = JSON.stringify({
                songId: currentSong.id,
                mode: replayGainMode,
                raw: calculation.gainDb,
                effective: calculation.effectiveGainDb,
                peak: calculation.peak ?? null,
            });

            if (replayGainLogSignatureRef.current !== replayGainLogSignature) {
                replayGainLogSignatureRef.current = replayGainLogSignature;
                console.log(`[AudioContext] ReplayGain mode=${replayGainMode} gain=${calculation.effectiveGainDb}dB (raw=${calculation.gainDb}dB, peak=${calculation.peak ?? 'n/a'}, linear=${calculation.linearGain.toFixed(2)})`);
            }
        } catch (error) {
            console.warn('[AudioContext] Failed to apply ReplayGain', error);
        }
    }, [audioContextRef, currentSong, gainNodeRef, getActiveChain, localSongs, replayGainMode]);

    const setupAudioAnalyzer = useCallback(() => {
        // The context is the sentinel, not the source node: a deck that failed to connect must
        // not let this run twice, because createMediaElementSource throws on a second call.
        if (!audioRef.current || audioContextRef.current) return;
        try {
            const AudioContextClass = window.AudioContext || (window as Window & typeof globalThis & { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
            const ctx = new AudioContextClass();
            audioContextRef.current = ctx;

            const analyser = ctx.createAnalyser();
            analyser.fftSize = 2048;
            analyser.smoothingTimeConstant = 0.6;
            analyserRef.current = analyser;

            const { gainNode, fadeNode, effectChain, outputGate, decksConnected } = buildPlaybackGraph({
                context: ctx,
                analyser,
                connectDecks,
                equalizerNodesRef,
                settings: heardEqualizerSettings,
            });
            gainNodeRef.current = gainNode;
            // Hands the transport its own fade node; until this runs (or if setup fails) pause and
            // resume see no graph and fall back to plain element pause/play.
            registerPlaybackFadeGraph({ context: ctx, gain: fadeNode });
            effectChainRef.current = effectChain;
            outputGateRef.current = outputGate;
            if (!decksConnected) {
                console.warn('[AudioContext] Deck setup incomplete, automix will stay idle');
            }
            applyReplayGain();
            syncOutputGain(getTargetPlaybackVolume(), 0);
        } catch (error) {
            console.error('Audio Context Setup Failed:', error);
        }
    }, [analyserRef, applyReplayGain, audioContextRef, audioRef, connectDecks, gainNodeRef, getTargetPlaybackVolume, heardEqualizerSettings, syncOutputGain]);

    /**
     * Writes a fully-played track's audio and cover into the media cache.
     *
     * Takes the song and its source explicitly rather than reading `currentSong`/`audioSrc`, because
     * the automix settle path caches the track that just faded OUT - and by then the app's own state
     * already names the track that arrived. `coverUrl` is passed for the currently-displayed track
     * (which may carry a user override); left undefined - the automix path - the cover is taken from
     * the song's own metadata instead.
     *
     * The writes themselves live in `cachePlayedTrackAssets`, which is where the audio and cover
     * conditions are kept apart from each other; this only adds the setting gate, which does not
     * belong in a service.
     */
    const cacheSongAssetsFor = useCallback(async (
        song: SongResult | null,
        src: string | null,
        coverUrl?: string | null,
    ) => {
        if (!enableMediaCache) return;

        await cachePlayedTrackAssets(song, src, coverUrl);
    }, [enableMediaCache]);

    const cacheSongAssets = useCallback(
        () => cacheSongAssetsFor(currentSong, audioSrc, getCoverUrl()),
        [audioSrc, cacheSongAssetsFor, currentSong, getCoverUrl],
    );

    // 打开原生引擎的唯一入口：自动播放和设置变化重开都走这里，参数永远取自当前 store。
    // 不能用 isLumaPlaying 当门槛——引擎从没打开过时它永远是 false，会把首次打开拦死。
    const openNativeEngine = useCallback(async (position: number) => {
        if (typeof window === "undefined" || !window.electron?.lumaAudio) return;
        const luma = useLumaAudioStore.getState();
        if (!luma.engineEnabled) return;
        const localSongId = (currentSong as SongResult & { localRef?: { songId: string } })?.localRef?.songId;
        const localData = localSongId ? localSongs.find(s => s.id === localSongId) : undefined;
        // BASS only takes real files. A local track's permission lives on the renderer's file
        // handle, and the in-app stream schemes (blob: and the per-platform ones) mean nothing
        // to the native engine, so every non-HTTP source is read to bytes here and staged on disk.
        let targetFile = audioSrc;
        const isRemoteHttp = typeof targetFile === 'string' && /^https?:\/\//i.test(targetFile);
        let needsStaging = Boolean(targetFile) && !isRemoteHttp;
        if (localData) {
            // 首选真实路径：曲库的文件句柄拿到的 File 能直接解析成绝对路径，
            // 引擎原位读取，不用把整首（DSD 可能几百 MB）读进内存再过一次 IPC。
            const file = await import('../../../../services/localMusicService')
                .then(module => module.getFileFromLocalSong(localData))
                .catch(() => null);
            const directPath = file ? window.electron?.webUtils?.getPathForFile(file) : '';
            if (directPath) {
                targetFile = directPath;
                needsStaging = false;
            }
        }
        if (needsStaging) {
            const bytes = localData
                // Loaded on demand: the local library pulls in the whole i18n/music stack,
                // and a static import here would drag it into every test that touches this hook.
                ? await import('../../../../services/localMusicService')
                    .then(module => module.getLocalSongArrayBuffer(localData))
                    .catch(() => null)
                : await fetch(targetFile!).then(res => (res.ok ? res.arrayBuffer() : null)).catch(() => null);
            if (!bytes) {
                const error = localData
                    ? "无法读取本地文件（请重新授权曲库文件夹）"
                    : "无法读取音频流";
                useLumaAudioStore.setState({ openError: error });
                console.warn('[LumaAudio] audio bytes unreadable:', localData?.id ?? targetFile);
                return;
            }
            const digest = await crypto.subtle.digest('SHA-256', bytes);
            const hash = Array.from(new Uint8Array(digest).slice(0, 12))
                .map(byte => byte.toString(16).padStart(2, '0'))
                .join('');
            const staged = await window.electron.lumaAudio.stageLocalFile(
                `${localData?.id ?? 'stream'}-${hash}`,
                bytes,
            );
            if (!staged.ok || !staged.path) {
                const error = staged.error || "无法为引擎准备本地文件";
                useLumaAudioStore.setState({ openError: error });
                console.warn('[LumaAudio] staging local file failed:', error);
                return;
            }
            targetFile = staged.path;
        }
        if (!targetFile) return;
        // 引擎失败时网页 <audio> 会继续出声，听起来"一切正常"——
        // 不把错误打出来用户就无从发现独占根本没打开。
        const res = await window.electron.lumaAudio.open({
            file: targetFile,
            backend: luma.backend,
            device: luma.activeDeviceIndex,
            mode: luma.dsdMode,
            pcmRate: luma.pcmRate,
            forceRate: resolveLumaForceRate(luma),
            volume: getTargetPlaybackVolume(),
            position,
            // 引擎接管时网页 fade 节点被钉在 0，淡入只能在引擎自己这边做。
            fadeIn: useAudioSettingsStore.getState().playbackFadeEnabled ? PLAYBACK_FADE_SECONDS : 0,
        });
        useLumaAudioStore.setState({ openError: res.ok ? "" : res.error || "unknown", engineOwnsOutput: res.ok });
        // A new stream starts its clock from scratch; anything sampled from the old one is stale.
        engineClockRef.current = null;
        // 引擎接管后，网页这一路只用来喂可视化和效果，不能再送到扬声器：
        // 否则共享模式会双份出声，独占模式下两条流还会互相抢设备。
        // 静音必须落在分析器之后的输出门，分析器才能继续拿到满幅信号。
        const context = audioContextRef.current;
        const gate = outputGateRef.current;
        if (gate && context) {
            gate.gain.cancelScheduledValues(context.currentTime);
            gate.gain.setValueAtTime(res.ok ? 0 : 1, context.currentTime);
        }
        if (!res.ok) console.warn('[LumaAudio] native engine open failed:', res.error);
    }, [audioContextRef, audioSrc, currentSong, gainNodeRef, getTargetPlaybackVolume, localSongs, syncOutputGain]);

    useEffect(() => {
        if (audioRef.current) {
            syncOutputGain(getTargetPlaybackVolume(), 0.015);
        }
        if (typeof window !== "undefined" && window.electron?.lumaAudio) {
            void window.electron.lumaAudio.setVolume(getTargetPlaybackVolume());
        }
    }, [audioRef, getTargetPlaybackVolume, syncOutputGain]);

    useEffect(() => {
        const lumaAudio = typeof window !== "undefined" ? window.electron?.lumaAudio : undefined;
        if (!lumaAudio) return;
        // 只有输出相关的设置才重开引擎：轮询每 50ms 写一次 playbackState（播放位置），
        // 无差别订阅等于每秒 20 次重开独占设备，直接把主进程和 UI 冻住。
        let lastConfig = '';
        let configDirty = false;
        let reopening = false;
        const configOf = (s: ReturnType<typeof useLumaAudioStore.getState>) =>
            // activeDeviceId 必须参与：两套枚举里 index 会撞（都从 0 起），只看 index
            // 切到另一台同号设备会被当成"没变"，引擎留在旧设备上。
            `${s.engineEnabled}|${s.backend}|${s.activeDeviceId}|${s.activeDeviceIndex}|${s.dsdMode}|${s.pcmRate}|${s.rateMode}`;
        const unsubscribe = useLumaAudioStore.subscribe(state => {
            const next = configOf(state);
            if (next === lastConfig) return;
            lastConfig = next;
            configDirty = true;
        });
        lastConfig = configOf(useLumaAudioStore.getState());
        let reopenPending = false;
        const maybeReopen = async () => {
            if (!configDirty || reopening) return;
            reopening = true;
            configDirty = false;
            try {
                const luma = useLumaAudioStore.getState();
                // 播放中才按新配置重开；暂停时改设置留给恢复播放的那次打开。
                if (luma.engineEnabled && luma.isLumaPlaying) {
                    // 换设备/换率时先淡出，避免旧设备被硬切产生的爆音。
                    if (useAudioSettingsStore.getState().playbackFadeEnabled) {
                        await window.electron?.lumaAudio?.fadeWait(0, PLAYBACK_FADE_SECONDS);
                    }
                    // 必须先释放旧会话再开新的：ASIO 驱动同一时刻只接受一个实例，
                    // WASAPI 独占句柄不放手时切共享/换率也会被拒。只靠 luma_open 内部的
                    // reset 已经太晚——新后端在那之前就开始要设备了。
                    await window.electron?.lumaAudio?.stop();
                    engineClockRef.current = null;
                    await openNativeEngine(currentTime.get());
                } else {
                    await window.electron?.lumaAudio?.stop();
                    useLumaAudioStore.setState({ openError: "", engineOwnsOutput: false });
                    const gate = outputGateRef.current;
                    const context = audioContextRef.current;
                    if (gate && context) {
                        gate.gain.cancelScheduledValues(context.currentTime);
                        gate.gain.setValueAtTime(1, context.currentTime);
                    }
                    syncOutputGain(getTargetPlaybackVolume(), 0);
                }
            } catch (error) {
                // 一次失败不能把后续切换永久锁死：openNativeEngine 内部已写 openError，
                // 这里只保证 reopening 归位。
                console.warn('[LumaAudio] reopen failed:', error);
            } finally {
                reopening = false;
                // 重开期间用户又改了配置：再跑一轮，落到最后那次选择上。
                if (configDirty && !reopenPending) {
                    reopenPending = true;
                    queueMicrotask(() => { reopenPending = false; void maybeReopen(); });
                }
            }
        };
        const timer = setInterval(async () => {
            void maybeReopen();
            const luma = useLumaAudioStore.getState();
            if (!luma.engineEnabled) return;
            try {
                const res = await lumaAudio.getState();
                if (res.ok && res.state) {
                    luma.updatePlaybackState(res.state);
                    if (typeof res.state.position === "number" && Number.isFinite(res.state.position) && res.state.playing) {
                        // 记录引擎位置与其到达时刻：两次轮询之间由本地时钟继续推进，
                        // 歌词和动画才不会因为 IPC 的 50ms 往返而落后于声音。
                        engineClockRef.current = {
                            position: res.state.position,
                            at: performance.now(),
                            sourceRate: res.state.rate ?? 0,
                        };
                        currentTime.set(res.state.position);
                    } else if (res.state.playing === false) {
                        // Paused or ended: stop extrapolating, and take the engine's final position
                        // so the picture settles where the sound actually stopped.
                        engineClockRef.current = null;
                        if (typeof res.state.position === "number" && Number.isFinite(res.state.position)) {
                            currentTime.set(res.state.position);
                        }
                    }
                    if (typeof res.state.duration === "number" && res.state.duration > 0) {
                        usePlaybackStore.getState().setDuration(res.state.duration);
                    }
                }
            } catch {}
        }, 50);
        return () => { unsubscribe(); clearInterval(timer); };
    }, [openNativeEngine]);

    useEffect(() => {
        // Between status polls the engine keeps playing, so the UI clock has to as well. A short
        // interval rather than requestAnimationFrame: this only has to feed the lyric/visual clock,
        // and the project's high frequency animation paths must not gain another per-frame writer.
        const timer = setInterval(() => {
            const luma = useLumaAudioStore.getState();
            const sample = engineClockRef.current;
            if (!luma.engineOwnsOutput || !sample || !luma.isLumaPlaying) return;
            const elapsed = (performance.now() - sample.at) / 1000;
            const duration = usePlaybackStore.getState().duration;
            const next = sample.position + elapsed;
            if (Number.isFinite(duration) && duration > 0 && next >= duration) return;
            currentTime.set(next);
        }, 50);
        return () => clearInterval(timer);
    }, []);

    useEffect(() => {
        localStorage.setItem('local_replaygain_mode', replayGainMode);
    }, [replayGainMode]);

    useEffect(() => {
        applyReplayGain();
    }, [applyReplayGain]);

    /*
     * Writes what is heard into the graph. A mod's change waits while an automix blend can be heard and is
     * then applied as it stands by then; any later change re-runs this, which drops the wait and applies at
     * once by its own origin (planAudioEqualizerApply).
     */
    useEffect(() => {
        heardEqualizerSettingsRef.current = heardEqualizerSettings;
        const context = audioContextRef.current;
        if (!context || equalizerNodesRef.current.length === 0) {
            return;
        }
        const plan = planAudioEqualizerApply({
            origin: equalizerOrigin,
            transitionAudible: isTransitionAudibleRef.current?.() ?? false,
        });
        const apply = (settings: typeof heardEqualizerSettings) => {
            applyAudioEqualizerSettings(context, equalizerNodesRef.current, settings, plan.timeConstant);
            effectChainRef.current?.apply(settings.effects, settings.enabled);
        };
        if (!plan.defer) {
            apply(heardEqualizerSettings);
            return;
        }
        const timer = setInterval(() => {
            if (isTransitionAudibleRef.current?.()) {
                return;
            }
            clearInterval(timer);
            apply(heardEqualizerSettingsRef.current);
        }, DEFERRED_EQUALIZER_POLL_MS);
        return () => clearInterval(timer);
    }, [audioContextRef, heardEqualizerSettings, equalizerOrigin]);

    useEffect(() => () => {
        effectChainRef.current?.dispose();
        effectChainRef.current = null;
    }, []);

    useEffect(() => {
        const audioElement = audioRef.current;
        if (!audioElement) {
            previousAudioSrcRef.current = audioSrc;
            return;
        }

        if (audioSrc && previousAudioSrcRef.current && previousAudioSrcRef.current !== audioSrc) {
            audioElement.pause();
            audioElement.load();
        }

        previousAudioSrcRef.current = audioSrc;
    }, [audioRef, audioSrc]);

    useEffect(() => {
        if (audioSrc && audioRef.current) {
            // `audioRef` and `audioSrc` describe the same track almost always - but not while a
            // blend is being armed. Automix repoints the ref at the incoming deck the instant it
            // arms, and `audioSrc` keeps naming the OUTGOING track until playSong has resolved the
            // next URL, which takes an await or two. Playing in that window starts whatever the
            // incoming deck still had loaded from an earlier song AND spends the autoplay intent
            // on it, so when the real source lands there is nothing left to press play with: the
            // deck sits fully buffered at currentTime 0 in silence.
            //
            // The deck's own src attribute is written from `audioSrc`, so an exact match is the
            // whole test. Bail WITHOUT spending the intent - this effect runs again on the commit
            // that gives the deck its real source, and that run is the one meant to start it.
            if (audioRef.current.getAttribute('src') !== audioSrc) return;

            // The deck has already failed on this exact source and something else - the transcode
            // fallback - is off resolving it. play() on an element in error state cannot start
            // anything; it rejects with NotSupportedError. Pressing it here only spends the intent,
            // so when the recovered source lands there is nothing left to start the deck with.
            // Bail WITHOUT spending it, the same way the autoplay hold below does: replacing the
            // source runs this effect again, and that run is the one meant to start it.
            if (audioRef.current.error) return;

            if (shouldAutoPlayRef.current && !isLyricsLoading) {
                // The deck has its source and is buffering, but its blend is not due yet. Bail
                // WITHOUT spending the intent - this effect runs again when the hold lifts, and
                // that run is the one meant to start it.
                //
                // This is what keeps the load out of the blend. Pressing play here instead starts
                // the fade wherever loading happened to finish, which took the same second or two
                // off every transition and capped an eight second plan at three.
                if (isAutoplayHeld) {
                    // The outgoing deck is still sounding. playSong dropped the state to IDLE on
                    // its way past and nothing else puts it back until this deck starts, so left
                    // alone the transport reads "stopped" for the whole lead - and the play button
                    // it offers would start this deck early and take the blend with it.
                    setPlayerState(PlayerState.PLAYING);
                    return;
                }

                shouldAutoPlayRef.current = false;

                // Automix starts the next track seconds early, so a pause can land after the
                // advance is already in flight and too late to recall. Honour the pause instead:
                // without this the listener presses pause and the next song starts anyway.
                if (suppressAutoplayRef.current) {
                    suppressAutoplayRef.current = false;
                    setPlayerState(PlayerState.PAUSED);
                    return;
                }

                syncOutputGain(getTargetPlaybackVolume(), 0);
                // 等引擎真的打开再启动 deck：否则 deck 先跑起来、引擎还在落盘/初始化，
                // 两套时钟从不同起点各走各的，切歌就会音画不同步。
                const engineOwned = mustWaitForNativeEngine(
                    typeof window !== "undefined" && Boolean(window.electron?.lumaAudio),
                    useLumaAudioStore.getState().engineEnabled,
                );
                // 引擎打开失败不该让整首歌放不出来：openNativeEngine 已把错误写进 store，
                // 这里照常启动网页 deck，播放降级为网页路径。
                const playPromise = engineOwned
                    ? openNativeEngine(0).catch(() => undefined).then(() => audioRef.current?.play())
                    : audioRef.current?.play();
                if (playPromise !== undefined) {
                    playPromise
                        .then(() => {
                            setPlayerState(PlayerState.PLAYING);
                            setupAudioAnalyzer();
                        })
                        .catch(error => {
                            if (audioRef.current && !audioRef.current.paused && !audioRef.current.ended) {
                                setPlayerState(PlayerState.PLAYING);
                                return;
                            }

                            // A load() landing between play() and its promise aborts the play.
                            // That happens whenever the source changes again while a track is
                            // still starting - skipping during an automix transition is the
                            // reliable way to see it - and the autoplay flag was spent before
                            // play() was ever called, so nothing would press play again and the
                            // app sits silent. Put the intent back for the next run.
                            if (error.name === 'AbortError') {
                                shouldAutoPlayRef.current = true;
                                return;
                            }

                            if (error.name === 'NotAllowedError') {
                                setStatusMsg({ type: 'info', text: t('status.clickToPlay') });
                                setPlayerState(PlayerState.PAUSED);
                            }
                        });
                }
            }
        }
        }, [audioRef, audioSrc, getTargetPlaybackVolume, isAutoplayHeld, isLyricsLoading, openNativeEngine, setPlayerState, setStatusMsg, setupAudioAnalyzer, shouldAutoPlayRef, suppressAutoplayRef, syncOutputGain, t]);

    // Tells this bridge that the active deck ALREADY holds `src` and is sounding it, so the next time
    // `audioSrc` settles on that value the reload effect above treats it as a no-op instead of calling
    // load(). Used when a transition is cancelled back onto the deck still playing the outgoing track:
    // that deck has the song loaded and playing, and reloading it would run the whole song-change
    // shape - a fresh load, a visible hitch - for a track that never actually left.
    const adoptActiveDeckSource = useCallback((src: string) => {
        previousAudioSrcRef.current = src;
    }, []);

    return {
        setupAudioAnalyzer,
        cacheSongAssets,
        cacheSongAssetsFor,
        adoptActiveDeckSource,
    };
}
