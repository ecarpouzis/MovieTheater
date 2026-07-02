#!/usr/bin/env node
// Phase-1 acceptance helper (docs/arcade-plan.md §10): mint an ArcadeCapabilityToken and open the
// signaling WebSocket THROUGH the ArcadeGateway, confirming a valid token reaches the coordinator
// (which replies with INIT, t=4) and that a bad token is refused (403) before any upstream traffic.
//
// This is the "verified with a script speaking t=4" check. It exercises the gateway + token only; it
// does NOT complete WebRTC. Needs the gateway + a CloudRetro coordinator running (Phase 0/1 up).
//
// Node 22+ (built-in WebSocket + crypto). No npm install.
//   node test-gateway.mjs --secret <ArcadeTokenSecret> --gateway wss://arcade.carpouzis.com
//   node test-gateway.mjs ... --mode expired   # expect 403 (token in the past)
//   node test-gateway.mjs ... --mode garbage    # expect 403 (malformed token)

import crypto from "node:crypto";

const args = Object.fromEntries(
  process.argv.slice(2).join(" ").split("--").filter(Boolean).map((s) => {
    const [k, ...v] = s.trim().split(/\s+/);
    return [k, v.join(" ") || true];
  })
);

const secret = args.secret;
const gateway = (args.gateway || "wss://arcade.carpouzis.com").replace(/\/$/, "");
const mode = args.mode || "valid";
if (!secret) { console.error("Missing --secret <ArcadeTokenSecret>"); process.exit(2); }

const b64url = (buf) => Buffer.from(buf).toString("base64").replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");

// Mirror MovieTheater.Core.ArcadeCapabilityToken.Mint exactly:
// payload = userId|gameId|roomCode|base64url(cloudRetroRoomId)|playerSlot|expiresUnixSeconds
function mintToken({ userId = 1, gameId = 1, roomCode = "TEST01", cloudRetroRoomId = "", playerSlot = 0, expiresUnixSeconds }) {
  const roomIdField = b64url(Buffer.from(cloudRetroRoomId, "utf8"));
  const payload = `${userId}|${gameId}|${roomCode}|${roomIdField}|${playerSlot}|${expiresUnixSeconds}`;
  const data = Buffer.from(payload, "utf8");
  const sig = crypto.createHmac("sha256", secret).update(data).digest();
  return `${b64url(data)}.${b64url(sig)}`;
}

const now = Math.floor(Date.now() / 1000);
let token;
if (mode === "garbage") token = "not-a-valid-token";
else if (mode === "expired") token = mintToken({ expiresUnixSeconds: now - 3600 });
else token = mintToken({ expiresUnixSeconds: now + 300 });

// Creator token → empty room_id (⇒ create). We only need to reach INIT, not actually start a game.
const url = `${gateway}/w/${token}?room_id=&zone=`;
console.log(`[${mode}] connecting: ${gateway}/w/<token>?room_id=&zone=`);

const ws = new WebSocket(url);
let gotInit = false;
const timeout = setTimeout(() => {
  console.error("✗ timed out with no message");
  process.exit(1);
}, 8000);

ws.addEventListener("open", () => console.log("· socket open"));
ws.addEventListener("message", (ev) => {
  let msg;
  try { msg = JSON.parse(ev.data); } catch { return; }
  console.log(`· recv t=${msg.t}`);
  if (msg.t === 4) {
    gotInit = true;
    clearTimeout(timeout);
    const ice = msg.p?.ice?.length ?? 0;
    const games = msg.p?.games?.length ?? 0;
    console.log(`✓ INIT (t=4) received — gateway forwarded to the coordinator. ice=${ice}, games=${games}.`);
    if (mode !== "valid") console.error(`✗ mode=${mode} unexpectedly reached the coordinator (should have been 403).`);
    ws.close();
    process.exit(mode === "valid" ? 0 : 1);
  }
});
ws.addEventListener("error", (e) => {
  clearTimeout(timeout);
  // A 403 from the gateway surfaces as a failed upgrade — the expected result for expired/garbage.
  if (mode === "valid") { console.error("✗ connection failed (valid token should reach t=4):", e.message || e); process.exit(1); }
  console.log(`✓ refused before upstream (expected for mode=${mode}).`);
  process.exit(0);
});
ws.addEventListener("close", () => {
  if (!gotInit && mode === "valid") { console.error("✗ closed before INIT."); process.exit(1); }
});
