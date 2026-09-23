import { readStored, writeStored } from "./utils/storage";

// How many audio channels a session should ASK the server for — the answer to "5.1 or stereo?" that
// both players' menus and every Stream/Start call share.
//
// Until 2026-09-22 the server floored every client at 6 channels (5.1) on the theory that every MSE
// browser decodes multichannel AAC and downmixes cleanly itself. Desktop browsers do. Chromium on
// Android does NOT: when the device advertises low-latency audio it opens a 6-channel AudioTrack and
// hands the downmix to the vendor's audio HAL — which on the household's Galaxy Tab S4 produced audio
// nobody could listen to on every 5.1 title, in Chrome and Edge alike, while Firefox (which always
// mixes to stereo in software) played the same streams fine. The channel probe can't tell those
// apart: AudioContext.destination.maxChannelCount reads the OS output config, not what the browser
// will do with six channels.
//
// So the rule is jellyfin-web's (getPhysicalAudioChannels): ask for surround only when the browser
// has proved it decodes Dolby (AC-3 / E-AC-3) — a browser with a real multichannel audio path — and
// stereo otherwise. Ziggy's Jellyfin downmixes with DownMixAudioBoost 1 (no volume filter), so a
// server-side stereo mix no longer clips the way it did when the floor was introduced. The viewer can
// override per browser: "Surround" for a stereo-probing desktop with 5.1 speakers, "Stereo" for a
// device that lies about its channels.
export const AUDIO_OUTPUT_KEY = "AudioOutput";

export const AUDIO_OUTPUT_OPTIONS = [
  { key: "auto", label: "Auto", hint: "surround when this browser decodes Dolby" },
  { key: "stereo", label: "Stereo", hint: "2.0 — mixed down on the server" },
  { key: "surround", label: "Surround", hint: "5.1 as AAC or Dolby" },
];

export function readAudioOutput() {
  const stored = readStored(AUDIO_OUTPUT_KEY);
  return AUDIO_OUTPUT_OPTIONS.some((o) => o.key === stored) ? stored : "auto";
}

export function writeAudioOutput(key) {
  writeStored(AUDIO_OUTPUT_KEY, key === "auto" ? null : key);
}

// The channel count to send. `caps` is the detectStreamCapabilities() shape (supportsAc3 /
// supportsEac3 / maxAudioChannels); `pref` one of the option keys. 7.1 (8) survives when the probe
// reports it and surround is on; nothing ever asks for more than 8 or fewer than 2.
export function requestedAudioChannels(caps, pref = "auto") {
  const probed = Number(caps?.maxAudioChannels) || 2;
  const surround = () => Math.min(8, Math.max(6, probed));
  if (pref === "stereo") return 2;
  if (pref === "surround") return surround();
  return caps?.supportsAc3 || caps?.supportsEac3 ? surround() : 2;
}

// Menu rows, with the active one marked — same shape as playerMenuModel's other option lists. With
// `caps` the Auto row says what Auto MEANS on this browser, so a 5.1-speaker desktop whose browser
// decodes no Dolby can see that it needs the Surround override rather than wonder where 5.1 went.
export function audioOutputOptions(current = readAudioOutput(), caps = null) {
  const autoHint = !caps
    ? AUDIO_OUTPUT_OPTIONS[0].hint
    : requestedAudioChannels(caps, "auto") >= 6
      ? "surround — this browser decodes Dolby"
      : "stereo — this browser decodes no Dolby; pick Surround for 5.1 speakers";
  return AUDIO_OUTPUT_OPTIONS.map((o) => ({ ...o, hint: o.key === "auto" ? autoHint : o.hint, selected: o.key === current }));
}
