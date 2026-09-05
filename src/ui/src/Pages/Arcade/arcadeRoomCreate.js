/**
 * Starting an arcade room, and the per-room stream quality the creator's device remembers.
 *
 * This lived inside ArcadePage until the saves vault became a page of its own (`/arcade/saves`):
 * "Resume" on a save has to start a room, and the lobby is no longer the only surface that starts
 * one. Nothing here is lobby state — it is a localStorage read, a capability probe and one POST —
 * so it moved out whole rather than being copied.
 */
import { message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { arcadeDeviceId } from "./cloudRetroClient";

// Per-room stream quality the creator picks (arcade per-room bitrate/FEC). Persisted so a friend
// group keeps its setting across sessions; applied to every room YOU start (one encoder per room =
// creator's choice). The dropdowns that write it (bitrate presets, network, codec) stay on the page.
export const QUALITY_KEY = "arcade.streamQuality";

// What each profile actually sends (the worker never sees "profiles", only these params).
// audioFec: 1 = on, 2 = off. paceMs: patch-0028 in-frame smoothing window (0 = off; 5G gets a
// wider window because big keyframes at low cellular bitrates benefit from more spread).
export const NETWORK_PROFILES = {
  lan: { audioFec: 1, paceMs: 0 },
  remote: { audioFec: 1, paceMs: 5 },
  "5g": { audioFec: 1, paceMs: 8 },
};

// Resolve "auto" to a concrete codec for THIS device. powerEfficient is the hardware-decode signal —
// smooth-but-software (dav1d on a big desktop) still reports smooth:true, and software AV1 is exactly
// the tablet failure mode Auto exists to dodge, so the bar is powerEfficient. 1920x1080@60 is the
// worst frame any lane sends today (capture); retro encodes larger canvases but at the same or lower
// pixel rate. Any probe failure (old browser, Firefox without webrtc-type support) falls back to av1
// — the status-quo default, so Auto can never be WORSE than before it existed.
export async function resolveAutoCodec() {
  try {
    const info = await navigator.mediaCapabilities.decodingInfo({
      type: "webrtc",
      video: { contentType: 'video/AV1; codecs="av01.0.08M.08"', width: 1920, height: 1080, bitrate: 12_000_000, framerate: 60 },
    });
    return info.supported && info.powerEfficient ? "av1" : "h264";
  } catch { return "av1"; }
}
export function loadQuality() {
  try {
    const q = JSON.parse(localStorage.getItem(QUALITY_KEY));
    if (q && typeof q.videoBitrateKbps === "number") {
      // Legacy audioFec-shaped values (pre network-profile) map to LAN — the old default behavior.
      const network = NETWORK_PROFILES[q.network] ? q.network : "lan";
      // Deliberate codec picks are NOT migrated to Auto — a chosen h264 often protects a JOINING
      // tablet, which a creator-device probe cannot see. Deliberate = the codecChosen flag (set only
      // by the Codec dropdown's own onChange), OR any stored "h264": av1 was the seeded default, so
      // an un-flagged "av1" means "never picked" and gets Auto — which resolves back to av1 on every
      // hardware-AV1 device and only changes behavior on the devices av1 was failing on.
      const codec = (q.codecChosen === true && (q.codec === "h264" || q.codec === "av1")) || q.codec === "h264"
        ? q.codec : "auto";
      // networkChosen: set ONLY by the Network dropdown's own onChange, never by seeding — it is
      // what lets an explicit "LAN · pace 0" beat the capture lane's server-side pace default.
      // Legacy values (no flag) stay "not chosen" so those users keep the lane defaults.
      // Both *Chosen flags must round-trip here: setQ persists {...prev, ...patch}, so a flag this
      // function drops would be erased from storage by the next unrelated quality change.
      return {
        videoBitrateKbps: q.videoBitrateKbps, network, codec,
        networkChosen: q.networkChosen === true, codecChosen: q.codecChosen === true,
      };
    }
  } catch { /* ignore */ }
  // Auto + LAN + Auto-codec. NOTE: a stored value is NOT migrated — someone who deliberately picked
  // "Balanced · 5 Mbps" on a thin uplink should not be silently moved to Auto (whose ceiling reaches
  // 14 Mbps on GameCube; ABR would walk it back, but the choice is theirs). They opt in by choosing
  // Auto once.
  return { videoBitrateKbps: 0, network: "lan", codec: "auto", networkChosen: false, codecChosen: false };
}
export function saveQuality(q) { try { localStorage.setItem(QUALITY_KEY, JSON.stringify(q)); } catch { /* ignore */ } }

/**
 * Start a room on `gameId` and drive the browser into it. `gameId` is an ArcadeGame row — one
 * VERSION of a title — which is why the lobby calls the same value `versionId` in its own code and
 * `MovieAPI.createArcadeRoom` calls it `gameId`; the saves vault reads it straight off a save row.
 *
 * The creator's stored quality is read FRESH here (so a change made in the quality pills a moment
 * ago wins), the network profile is unbundled into the wire params — the server and worker stay
 * profile-agnostic — and "auto" is resolved to a concrete codec, because the room's encoder needs
 * one. `paceMs` is sent ONLY for a deliberate dropdown pick: omitting it (server null) keeps the
 * lane defaults (capture 8, GL 0), while an explicit LAN 0 must actually reach the server to beat
 * the capture default.
 *
 * Resolves to the descriptor it pushed with, or null when no room was started (the caller clears
 * its own "creating" state in a finally).
 */
export function createRoomAndGo(gameId, opts, history) {
  const q = loadQuality();
  const net = NETWORK_PROFILES[q.network] || NETWORK_PROFILES.lan;
  const netParams = q.networkChosen ? net : { audioFec: net.audioFec };
  return Promise.resolve(q.codec === "auto" ? resolveAutoCodec() : q.codec)
    .then((codec) => MovieAPI.createArcadeRoom(gameId, { ...opts, videoBitrateKbps: q.videoBitrateKbps, ...netParams, videoCodec: codec, deviceId: arcadeDeviceId() }))
    .then(async (r) => {
      if (r.status === 503) { message.warning("The arcade is full — every machine is in use. Try again shortly."); return null; }
      if (!r.ok) { message.error("Couldn't start that game."); return null; }
      return r.json();
    })
    .then((descriptor) => {
      if (descriptor) history.push({ pathname: `/arcade/room/${descriptor.roomCode}`, state: { descriptor } });
      return descriptor || null;
    })
    .catch(() => { message.error("Couldn't start that game."); return null; });
}
