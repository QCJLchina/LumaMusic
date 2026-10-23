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
        init: state.init,
        refreshDevices: state.refreshDevices,
        setEngineEnabled: state.setEngineEnabled,
        setBackend: state.setBackend,
        setDevice: state.setDevice,
        setDsdMode: state.setDsdMode,
        setPcmRate: state.setPcmRate,
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
                                luma.setBackend(Number(value));
                                void luma.refreshDevices();
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
                    <Row sub option={OPT_LUMA_DSD_MODE}>
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
                                    { value: "352800", label: "352.8 kHz" },
                                ]}
                                onChange={value => luma.setPcmRate(Number(value))}
                            />
                        </Row>
                    )}
                </>
            )}
        </>
    );
};

export default LumaAudioEngineGroup;
