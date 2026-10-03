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

// Resolve "auto" to a concrete codec for THIS device. powerEfficient is the hardware-decode signal, so
// BOTH codecs are probed and a hardware decoder wins: hardware AV1 first, else hardware H.264 (the
// tablet case: software dav1d on a tablet CPU can't keep up with 1280x1056@60, MediaCodec H.264 can).
//
// When NEITHER is hardware the tie goes to AV1 on a desktop. That is desktop Firefox on every machine:
// its WebRTC decodes AV1 with dav1d and H.264 with OpenH264, both software, and it reports both as
// supported+smooth, not powerEfficient. Measured 2026-10-02 on a DOS room (1920x1440 @70, pointer
// moving): dav1d 1.45 ms/frame, 77 fps, pli 0; OpenH264 drowned during boot and patch 0047 shrank the
// room to 1280x960 + dedup (10 fps, 46 ms jitter buffer). The old rule (AV1 only if powerEfficient)
// sent every desktop Firefox to the decoder that drowns. A phone/tablet with no hardware decoder for
// either keeps H.264 (lighter per pixel on a small CPU, and IDRs bound any backlog).
//
// 1920x1080@60 is the probe frame; the contentType carries no H.264 fmtp because Firefox answers
// "unsupported" for a parameterised H.264 type it decodes fine. Any probe failure falls back to the
// receiver's RTP capabilities: AV1 when the browser can receive it at all, else H.264.
const PROBE_FRAME = { width: 1920, height: 1080, bitrate: 12_000_000, framerate: 60 };

function isMobileDevice() {
  const nav = typeof navigator !== "undefined" ? navigator : {};
  if (typeof nav.userAgentData?.mobile === "boolean") return nav.userAgentData.mobile;
  // iPadOS reports a desktop Mac UA; touch points are the only tell.
  return /Android|Mobi|iPhone|iPad/i.test(nav.userAgent || "") || (/Macintosh/.test(nav.userAgent || "") && nav.maxTouchPoints > 1);
}

function canReceive(mime) {
  try { return RTCRtpReceiver.getCapabilities("video").codecs.some((c) => c.mimeType.toLowerCase() === mime); }
  catch { return null; }
}

// "SsP" = supported / smooth / powerEfficient, "-" where false; "?" when the probe itself failed.
const flags = (r) => (r ? `${r.supported ? "S" : "-"}${r.smooth ? "s" : "-"}${r.powerEfficient ? "P" : "-"}` : "?");

/**
 * The Auto decision plus a one-line summary of what this browser reported — e.g.
 * `"av1:Ss- h264:SsP h265:SsP m0 auto=h264"` — which the server keeps on the session row as codec-population
 * evidence (H.265 is probed for that evidence only; Auto never picks it).
 */
export async function decideAutoCodec() {
  const mobile = isMobileDevice();
  let av1 = null, h264 = null, h265 = null, codec;
  try {
    const probe = (contentType) => navigator.mediaCapabilities.decodingInfo({ type: "webrtc", video: { contentType, ...PROBE_FRAME } });
    [av1, h264, h265] = await Promise.all([
      probe('video/AV1; codecs="av01.0.08M.08"'), probe("video/H264"), probe("video/H265").catch(() => null),
    ]);
    if (av1.supported && av1.powerEfficient) codec = "av1";
    else if (h264.supported && h264.powerEfficient) codec = "h264";
    else if (av1.supported && (!h264.supported || !mobile)) codec = "av1";
    else codec = h264.supported ? "h264" : "av1";
  } catch {
    codec = canReceive("video/av1") === false ? "h264" : "av1";
  }
  return { codec, probe: `av1:${flags(av1)} h264:${flags(h264)} h265:${flags(h265)} m${mobile ? 1 : 0} auto=${codec}` };
}

export async function resolveAutoCodec() {
  return (await decideAutoCodec()).codec;
}

/**
 * Can this browser receive a room's codec at all? `null` = unknown (no RTP capability API) — callers must
 * then proceed, never refuse. A room with no recorded codec ("") is unknown too: it runs the worker default.
 */
export function canReceiveCodec(codec) {
  if (codec !== "av1" && codec !== "h264") return null;
  return canReceive(codec === "av1" ? "video/av1" : "video/h264");
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
  return Promise.resolve(q.codec === "auto" ? decideAutoCodec() : { codec: q.codec, probe: undefined })
    .then(({ codec, probe }) => MovieAPI.createArcadeRoom(gameId, {
      ...opts, videoBitrateKbps: q.videoBitrateKbps, ...netParams, videoCodec: codec, codecProbe: probe, deviceId: arcadeDeviceId(),
    }))
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

/**
 * Chromium's Local Network Access permission for this site: "granted" / "prompt" / "denied", or null where the
 * browser has no such permission (or names it differently — the name has changed across Chromium versions, and
 * an unknown name throws). On the home network the arcade/media hosts resolve to a private address, so a
 * browser that hasn't granted it never connects — and looks exactly like a dead room.
 */
export async function localNetworkPermission() {
  if (typeof navigator === "undefined" || !navigator.permissions?.query) return null;
  for (const name of ["local-network-access", "local-network"]) {
    try { return (await navigator.permissions.query({ name })).state; } catch { /* unknown name here */ }
  }
  return null;
}

const CODEC_NAME = { av1: "AV1", h264: "H.264" };

/** What to tell a player whose room started but never showed a picture (cloudRetroClient's watchdog). */
export function videoProblemMessage({ kind, codec } = {}, lnaState = null) {
  const name = CODEC_NAME[codec] || "this room's";
  if (kind === "mismatch") {
    return "This room's video settings changed while you were joining. Go back and join the room again.";
  }
  if (kind === "codec" || kind === "not-decoding") {
    const other = codec === "av1" ? "H.264" : "AV1";
    return `The game is running, but this browser can't play ${name} video. Ask the host to restart the game with Codec: ${other}.`;
  }
  if (lnaState === "denied" || lnaState === "prompt") {
    return "The game is running, but this browser is blocking the connection to the game server on your home network. "
      + "Allow \"local network access\" for this site (the icon at the left of the address bar), then rejoin.";
  }
  return "The game is running, but no picture is reaching this browser. Rejoin the room; if it keeps happening, "
    + "this network may be blocking the stream.";
}
