import { useCallback, useEffect, useMemo, useState } from "react";
import { castProfileFor, castCapabilities } from "./castProfiles";
import { readStored, writeStored } from "./utils/storage";

// The receiver-specific settings, shared by both players so they can't answer the same question two
// ways. Which profile a TV is on decides whether the picture appears at all, so a viewer who fixes it
// on the Watch page must not have to fix it again on /tv.
//
// The profile is stored per RECEIVER (a house can hold a 3rd-gen Chromecast in the bedroom and a
// Google TV in the lounge, and the right answer differs), keyed by friendly name — the only stable,
// human-meaningful identifier the sender SDK reliably hands out. Dolby pass-through is one setting
// for the account instead, because it describes the viewer's amplifier, not any one dongle.
const profileStorageKey = (deviceName) => `CastProfile:${deviceName || "default"}`;
const DOLBY_KEY = "CastDolbyPassthrough";

/**
 * @param device the connected receiver as described by useCastSender (null when not connected)
 * @returns { profile, deviceName, dolby, capabilities, selectProfile, toggleDolby }
 *   `capabilities` is the payload for /API/Stream/Start — the RECEIVER's decode profile, not this
 *   browser's. See castProfiles.js for why that distinction is the whole ballgame.
 */
export function useCastProfile(device) {
  const deviceName = device?.friendlyName || null;
  const modelName = device?.modelName || null;
  // Re-read per device rather than holding one value: the override belongs to the TV, not the tab.
  const [override, setOverride] = useState(null);
  const [dolby, setDolby] = useState(() => readStored(DOLBY_KEY) === "1");

  useEffect(() => {
    setOverride(deviceName ? readStored(profileStorageKey(deviceName)) : null);
  }, [deviceName]);

  const profile = useMemo(() => castProfileFor({ modelName, override }), [modelName, override]);

  const selectProfile = useCallback(
    (key) => {
      if (deviceName) writeStored(profileStorageKey(deviceName), key);
      setOverride(key);
    },
    [deviceName]
  );

  const toggleDolby = useCallback(() => {
    setDolby((on) => {
      writeStored(DOLBY_KEY, on ? null : "1");
      return !on;
    });
  }, []);

  const capabilities = useMemo(
    () => castCapabilities(profile, { dolbyPassthrough: dolby }),
    [profile, dolby]
  );

  // One string that changes exactly when the negotiation would come out differently. Both players
  // use it to decide whether a settings change needs a fresh session (it does — the profile is baked
  // into the DeviceProfile the server built) rather than diffing the pieces themselves.
  const negotiationKey = `${profile.key}:${dolby ? "dolby" : "aac"}`;

  return { profile, deviceName, dolby, capabilities, negotiationKey, selectProfile, toggleDolby };
}
