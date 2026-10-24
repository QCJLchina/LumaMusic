import React, { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { useShallow } from "zustand/react/shallow";
import { useLumaAudioStore } from "../../../../stores/useLumaAudioStore";
import { Row, Segmented, Select, Toggle, option, type SettingsGroupDef } from "../kit";

// src/components/modal/settings/pages/LumaAudioEngineGroup.tsx
// The native C++ / BASS / ASIO / DSD engine controls of the 播放控制 page.

const SOUND_GROUP: SettingsGroupDef = { id: "audioOutputSettings", titleKey: "options.settingsHeadingSound" };
const OPT_LUMA_ENGINE = option(SOUND_GROUP, "lumaEngine", "options.lumaEngine", { electronOnly: true });
const OPT_LUMA_BACKEND = option(SOUND_GROUP, "lumaBackend", "options.lumaBackend", { electronOnly: true });
const OPT_LUMA_DEVICE = option(SOUND_GROUP, "lumaDevice", "options.lumaDevice", { electronOnly: true });
const OPT_LUMA_DSD_MODE = option(SOUND_GROUP, "lumaDsdMode", "options.lumaDsdMode", { electronOnly: true });
const OPT_LUMA_PCM_RATE = option(SOUND_GROUP, "lumaPcmRate", "options.lumaPcmRate", { electronOnly: true });
const OPT_LUMA_RATE_MODE = option(SOUND_GROUP, "lumaRateMode", "options.lumaRateMode", { electronOnly: true });
const OPT_LUMA_OUTPUT_STATE = option(SOUND_GROUP, "lumaOutputState", "options.lumaOutputState", { electronOnly: true });

const LumaAudioEngineGroup: React.FC = () => {
    const { t } = useTranslation();
    const luma = useLumaAudioStore(useShallow(state => ({
        isAvailable: state.isAvailable,
        engineEnabled: state.engineEnabled,
        backend: state.backend,
        devices: state.devices,
        activeDeviceIndex: state.activeDeviceIndex,
        activeDeviceName: state.activeDeviceName,
        dsdMode: state.dsdMode,
        pcmRate: state.pcmRate,
        rateMode: state.rateMode,
        playbackState: state.playbackState,
        openError: state.openError,
        init: state.init,
        refreshDevices: state.refreshDevices,
        setEngineEnabled: state.setEngineEnabled,
        setBackend: state.setBackend,
        setDevice: state.setDevice,
        setDsdMode: state.setDsdMode,
        setPcmRate: state.setPcmRate,
        setRateMode: state.setRateMode,
    })));

    useEffect(() => {
        if (luma.isAvailable) return;
        void luma.init();
    }, [luma]);

    if (!luma.isAvailable) {
        return null;
    }

    const backendDevices = luma.devices.filter(device => device.backend === luma.backend);
    const deviceOptions = backendDevices.length > 0
        ? backendDevices.map(device => ({ value: String(device.index), label: device.name }))
        : [{ value: String(luma.activeDeviceIndex), label: luma.activeDeviceName || t("options.audioOutputDefault") }];
    const formatRate = (rate: number) => `${(rate / 1000).toFixed(rate % 1000 === 0 ? 0 : 1)} kHz`;
    const backendName = luma.playbackState.backend === 0
        ? t("options.lumaBackendShared")
        : luma.playbackState.backend === 1
            ? t("options.lumaBackendExclusive")
            : luma.playbackState.backend === 2 ? "ASIO" : "";
    const outputBits = luma.playbackState.deviceBits ? ` · ${luma.playbackState.deviceBits} bit` : "";
    const outputState = luma.playbackState.rate && backendName
        ? `${backendName} · ${formatRate(luma.playbackState.rate)}${outputBits}`
        : luma.openError
            ? t("options.lumaOutputFailed")
            : luma.playbackState.backend === undefined
                ? t("options.lumaOutputIdle")
                : "—";

    return (
        <>
            <Row option={OPT_LUMA_ENGINE}>
                <Toggle checked={luma.engineEnabled} onChange={luma.setEngineEnabled} />
            </Row>
            {luma.engineEnabled && (
                <>
                    <Row sub option={OPT_LUMA_BACKEND}>
                        <Segmented
                            value={String(luma.backend)}
                            options={[
                                { value: "0", label: t("options.lumaBackendShared") },
                                { value: "1", label: t("options.lumaBackendExclusive") },
                                { value: "2", label: t("options.lumaBackendAsio") },
                            ]}
                            onChange={value => {
                                void luma.setBackend(Number(value)).catch(error => {
                                    console.warn('[LumaAudio] backend switch failed:', error);
                                });
                            }}
                        />
                    </Row>
                    <Row sub option={OPT_LUMA_DEVICE}>
                        <Select
                            value={String(luma.activeDeviceIndex)}
                            options={deviceOptions}
                            onChange={value => {
                                const device = backendDevices.find(d => String(d.index) === value);
                                if (device) luma.setDevice(device);
                            }}
                            onOpen={() => void luma.refreshDevices()}
                        />
                    </Row>
                    <Row
                        sub
                        option={OPT_LUMA_DSD_MODE}
                        // 原生 DSD 是 BASS 的硬约束：必须走 ASIO 厂商驱动，WASAPI 上只有 DoP。
                        hint={luma.dsdMode === 2 && luma.backend !== 2 ? t("options.lumaDsdNativeNeedsAsio") : undefined}
                    >
                        <Segmented
                            value={String(luma.dsdMode)}
                            options={[
                                { value: "0", label: t("options.lumaDsdPcm") },
                                { value: "1", label: "DoP" },
                                { value: "2", label: t("options.lumaDsdNative") },
                            ]}
                            onChange={value => luma.setDsdMode(Number(value))}
                        />
                    </Row>
                    {luma.backend !== 0 && luma.dsdMode === 0 && (
                        <Row sub option={OPT_LUMA_RATE_MODE}>
                            <Segmented
                                value={String(luma.rateMode)}
                                options={[
                                    { value: "0", label: t("options.lumaRateFollowSource") },
                                    { value: "1", label: t("options.lumaRateBySetting") },
                                ]}
                                onChange={value => luma.setRateMode(Number(value))}
                            />
                        </Row>
                    )}
                    {luma.dsdMode === 0 && (
                        <Row sub option={OPT_LUMA_PCM_RATE}>
                            <Select
                                value={String(luma.pcmRate)}
                                options={[
                                    { value: "44100", label: "44.1 kHz" },
                                    { value: "48000", label: "48 kHz" },
                                    { value: "88200", label: "88.2 kHz" },
                                    { value: "96000", label: "96 kHz" },
                                    { value: "176400", label: "176.4 kHz" },
                                    { value: "192000", label: "192 kHz" },
                                    { value: "352800", label: "352.8 kHz" },
                                    { value: "384000", label: "384 kHz" },
                                ]}
                                onChange={value => luma.setPcmRate(Number(value))}
                            />
                        </Row>
                    )}
                    <Row sub option={OPT_LUMA_OUTPUT_STATE} hint={luma.openError}>
                        <span className="text-sm text-[color:var(--st-t2)]">{outputState}</span>
                    </Row>
                </>
            )}
        </>
    );
};

export default LumaAudioEngineGroup;
