// src/components/app/playback/bridges/nativeEngineStartGate.ts
// Whether a track start waits for the native engine before the web deck may run.

/**
 * Both the deck and the engine keep a clock. Letting the deck run first leaves it ahead of the
 * engine by however long the engine takes to stage the file and open the device - which is the
 * lyrics drift heard after changing tracks in exclusive mode.
 */
export const mustWaitForNativeEngine = (hasBridge: boolean, engineEnabled: boolean): boolean =>
    hasBridge && engineEnabled;
