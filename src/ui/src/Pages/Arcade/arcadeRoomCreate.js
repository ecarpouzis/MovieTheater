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
// When NEITHER is hardware the tie goes to H.264 (the rule since Phase 4): a software H.264 decoder is the
// lighter one per pixel, and the tablet failure that created Auto was software AV1. Which browsers land here
// is a property of the browser BUILD, not the brand — the installed Firefox 157 on the arcade host reports
// both codecs powerEfficient and takes the hardware-AV1 branch, while Playwright's patched Firefox build
// reports both as software. Probe the real browser before reasoning about one.
// The exception: a browser that can't decode H.264 at all (Firefox without the OpenH264 plugin) gets AV1
// rather than no video.
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
 *
 * `avoid` is this device's own history (server `/API/Arcade/CodecHint`): a codec it recently drowned on or
 * refused. The probe can't see that — a tablet's Chrome reports software AV1 as `smooth` — so when the probe
 * lands on the avoided codec and the browser supports the other one, the other one wins (`hint=avoid-av1`).
 */
// ── The probe is a SNAPSHOT of the browser's own capability report, and that report can be late ─────────────
// Firefox's GPU process works out hardware decode support on a background task after it starts (GPUParent::RecvInit
// -> MCSInfo::GetSupportFromFactory) and again on every gfxVar update (RecvUpdateVar force-refreshes); until that
// report lands, decodingInfo(type:"webrtc") answers from an empty set, and Firefox treats a failed/empty platform
// answer as "software only" (WebrtcVideoDecoderFactory::SupportsCodec). So an early probe on a hardware machine can
// say "software" — never the reverse: a false "hardware" cannot happen. Hence: probe as soon as the lobby mounts,
// probe again at Play, and keep the MOST capable answer seen recently (PROBE_MEMORY_MS — hardware decode CAN go
// away mid-session, e.g. after a GPU-process crash, so the memory expires). If both codecs read software-only and
// the first probe is younger than PROBE_SETTLE_MS, wait out the rest and probe again: the rule is time since the
// FIRST probe, so a lobby probe and an immediate Play click both landing before the report still get a retry.
const PROBE_SETTLE_MS = 2000;
const PROBE_MEMORY_MS = 10 * 60_000;
let bestProbe = null;   // { av1, h264, h265 } — field-wise most capable answers seen
let firstProbeAt = 0;   // when the remembered window opened (Date.now ms)

function mergeAnswer(a, b) {
  if (!a) return b;
  if (!b) return a;
  return { supported: a.supported || b.supported, smooth: a.smooth || b.smooth, powerEfficient: a.powerEfficient || b.powerEfficient };
}

async function probeOnce() {
  if (bestProbe && Date.now() - firstProbeAt > PROBE_MEMORY_MS) bestProbe = null;
  if (!bestProbe) firstProbeAt = Date.now();
  const probe = (contentType) => navigator.mediaCapabilities.decodingInfo({ type: "webrtc", video: { contentType, ...PROBE_FRAME } });
  const [av1, h264, h265] = await Promise.all([
    probe('video/AV1; codecs="av01.0.08M.08"'), probe("video/H264"), probe("video/H265").catch(() => null),
  ]);
  bestProbe = bestProbe
    ? { av1: mergeAnswer(bestProbe.av1, av1), h264: mergeAnswer(bestProbe.h264, h264), h265: mergeAnswer(bestProbe.h265, h265) }
    : { av1, h264, h265 };
  return bestProbe;
}

/** Start a capability probe early (the lobby calls this on mount) so a late browser report is already in by Play. */
export function primeCodecProbe() {
  try { if (navigator?.mediaCapabilities?.decodingInfo) probeOnce().catch(() => {}); } catch { /* no API */ }
}

/** Test seam: forget the page-session memory. */
export function resetCodecProbeMemory() { bestProbe = null; firstProbeAt = 0; }

export async function decideAutoCodec(avoid = null) {
  const mobile = isMobileDevice();
  let av1 = null, h264 = null, h265 = null, codec;
  try {
    ({ av1, h264, h265 } = await probeOnce());
    const sinceFirst = Date.now() - firstProbeAt;
    if (!av1.powerEfficient && !h264.powerEfficient && sinceFirst < PROBE_SETTLE_MS) {
      await new Promise((r) => setTimeout(r, PROBE_SETTLE_MS - sinceFirst));
      ({ av1, h264, h265 } = await probeOnce());
    }
    if (av1.supported && av1.powerEfficient) codec = "av1";
    else if (h264.supported && h264.powerEfficient) codec = "h264";
    else codec = h264.supported ? "h264" : "av1";
  } catch {
    codec = canReceive("video/av1") === false ? "h264" : "av1";
  }
  let hinted = "";
  if (avoid && avoid === codec) {
    const other = codec === "av1" ? "h264" : "av1";
    const otherProbe = other === "av1" ? av1 : h264;
    const otherOk = otherProbe ? otherProbe.supported : canReceive(`video/${other}`) !== false;
    if (otherOk) { codec = other; hinted = ` hint=avoid-${avoid}`; }
  }
  return { codec, probe: `av1:${flags(av1)} h264:${flags(h264)} h265:${flags(h265)} m${mobile ? 1 : 0} auto=${codec}${hinted}` };
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
 * one. `paceMs` is sent ONLY for a deliberate dropdown pick: omitting it (server null) means
 * unpaced on both lanes (the capture lane's default of 8 was dropped 2026-10-04); an explicit pick
 * is sent as chosen.
 *
 * Resolves to the descriptor it pushed with, or null when no room was started (the caller clears
 * its own "creating" state in a finally).
 */
export function createRoomAndGo(gameId, opts, history) {
  const q = loadQuality();
  const net = NETWORK_PROFILES[q.network] || NETWORK_PROFILES.lan;
  const netParams = q.networkChosen ? net : { audioFec: net.audioFec };
  const auto = () => MovieAPI.getArcadeCodecHint(arcadeDeviceId()).then((h) => decideAutoCodec(h && h.avoid));
  return Promise.resolve(q.codec === "auto" ? auto() : { codec: q.codec, probe: undefined })
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
  codec = codec || "av1"; // a room with no recorded codec runs the worker default, AV1
  const name = CODEC_NAME[codec] || "this room's";
  if (kind === "no-connection") {
    if (lnaState === "prompt") {
      return "Your browser is asking permission to reach the game server on your home network. Look for the prompt "
        + "by the address bar and choose Allow — the game starts as soon as you do.";
    }
    if (lnaState === "denied") {
      return "Your browser is blocking the game server on your home network. Allow \"local network access\" for this "
        + "site (the icon at the left of the address bar), then rejoin.";
    }
    return "Can't reach the game server yet. If this doesn't clear in a few seconds, this network may be blocking it.";
  }
  if (kind === "mismatch") {
    return "This room's video settings changed while you were joining. Go back and join the room again.";
  }
  if (kind === "decoder-wedged") {
    // decoderWedge.js gave up: this device's decoder stopped mid-room and two reconnects did not bring it back.
    const other = codec === "av1" ? "H.264" : "AV1";
    return `The picture keeps freezing on this device — its ${name} video decoder stopped, and reconnecting didn't bring it `
      + `back. Leave the room and start the game again with Codec: ${other} (or ask the host to).`;
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
