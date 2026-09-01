import { useEffect, useState } from "react";

// ── Can this browser reach the media plane — and by which path? ──────────────────────────────────
// The app and the bytes are served from two different places. The 2026-09-01 fiber cutover put the
// StreamGateway behind carrier-grade NAT, so the DIRECT path to it is IPv6; since the VPS door went
// in (docs/site-ipv4-door.md), IPv4-only visitors reach it too — via a relay that DNATs 443 down a
// WireGuard tunnel. That door turned the old hard failure ("v4-only plays nothing") into a soft one:
// everything works, but the relay adds a detour and spends the relay's bandwidth, which matters when
// a watch party multiplies it.
//
// So this now asks TWO questions, both once per tab, both before anyone presses play:
//   1. Can you open a connection to the host the bytes come from at all?     (as before)
//   2. If yes — can you ALSO reach an IPv6-only name on the same apex?
// A browser that passes 2 has working IPv6, and Happy Eyeballs is therefore already taking it
// straight to the media host; a browser that fails 2 while passing 1 is riding the relay, and is
// exactly who the "turn on IPv6" tip is for. Both are REACHABILITY tests, not protocol tests, and
// stay correct if the topology changes again.
//
// ⚠ `no-cors` is deliberate on both probes. They are cross-origin and neither endpoint sends CORS
// headers, so an ordinary fetch would reject on the CORS check even when the host is perfectly
// reachable. An opaque response tells us nothing ABOUT the response, which is fine: the only
// question is whether a connection could be opened. Resolve = reachable, reject = not.

const PROBE_TIMEOUT_MS = 6000;
// The v6 probe fails FAST on a v4-only network (no AAAA route), so it gets a shorter leash. A
// TIMEOUT there is deliberately read as "ok" (silent): it is ambiguous — a slow network, not
// proof the visitor lacks IPv6 — and the cost asymmetry favors a missed tip over telling someone
// with working IPv6 to go change their router (review finding, 2026-09-01). Only an outright
// rejection (no route / connection refused) earns the tip.
const V6_PROBE_TIMEOUT_MS = 4000;
// "unreachable" is re-checked in place rather than trusted forever: the audience is hotel wifi,
// where the first probe often fires BEFORE the captive portal is clicked through. The warning
// must clear itself once media becomes reachable — without this, one transient failure pinned a
// non-closable banner for the tab's whole life (review finding, 2026-09-01).
const UNREACHABLE_RETRY_MS = 30000;

// One verdict per TAB, and deliberately NOT via utils/storage: that helper is localStorage, and this
// verdict is a property of the NETWORK, not of the user. A laptop carried from a v4-only office to a
// dual-stack home must not arrive still believing it is cut off (or still nagging about IPv6).
// sessionStorage dies with the tab, which is exactly the lifetime this fact has. Storage can throw
// outright (private mode, storage disabled), so both ends are guarded — a browser that refuses
// storage simply re-probes.
const CACHE_KEY = "mediaReachable";

function readVerdict() {
  try {
    return window.sessionStorage.getItem(CACHE_KEY);
  } catch {
    return null;
  }
}

function writeVerdict(value) {
  try {
    window.sessionStorage.setItem(CACHE_KEY, value);
  } catch {
    /* storage blocked — we just probe again next mount */
  }
}

/** @returns {"yes"|"no"|"timeout"} — a timeout is NOT a "no": it is ambiguity, and each caller
 *  decides which way ambiguity falls. */
function probe(url, timeoutMs) {
  const control = new AbortController();
  const timer = setTimeout(() => control.abort(), timeoutMs);
  return fetch(url, { mode: "no-cors", cache: "no-store", signal: control.signal })
    .then(() => "yes", () => (control.signal.aborted ? "timeout" : "no"))
    .finally(() => clearTimeout(timer));
}

// The v6-only probe host is DERIVED, not hardcoded: `mediav6.` + the media base's apex
// (stream.example.com -> mediav6.example.com). The name carries an AAAA and deliberately no A —
// reaching it IS the answer. Served by the media host's Caddy as an empty 204; the DDNS task keeps
// its AAAA current alongside the others. A media base without a subdomain shape (an IP, a bare
// apex, dev boxes) yields null and the second question is simply not asked.
function v6ProbeUrl(mediaBase) {
  try {
    const host = new URL(mediaBase).hostname;
    const parts = host.split(".");
    if (parts.length < 3 || /^[0-9.]+$/.test(host)) return null;
    return `https://mediav6.${parts.slice(1).join(".")}/`;
  } catch {
    return null;
  }
}

/**
 * @returns {"checking"|"ok"|"ok-v4"|"unreachable"|"unknown"}
 *  ok          — media reachable, and the visitor has working IPv6 (direct path)
 *  ok-v4       — media reachable, but only over IPv4: they are riding the relay
 *  unreachable — media host unreachable on ANY route: nothing will play
 *  unknown     — no gateway configured (dev), or the app itself is unreachable
 */
export default function useMediaReachable() {
  const [state, setState] = useState(() => {
    // A cached "unreachable" (including one written by an older bundle) is never trusted on
    // mount — it re-probes instead, so the warning can only exist alongside a live failing probe.
    const cached = readVerdict();
    return cached && cached !== "unreachable" ? cached : "checking";
  });

  useEffect(() => {
    if (state !== "checking") return undefined;
    let alive = true;
    let retry = null;
    const settle = (verdict) => {
      if (!alive) return;
      writeVerdict(verdict);
      setState(verdict);
    };
    // "unreachable" is shown but NEVER cached, and re-probes on a timer: a captive portal clicked
    // through after first load must clear the warning in the same tab, not at the next one.
    const settleUnreachable = () => {
      if (!alive) return;
      setState("unreachable");
      retry = setTimeout(() => { if (alive) setState("checking"); }, UNREACHABLE_RETRY_MS);
    };

    (async () => {
      let base = null;
      try {
        const r = await fetch("/API/Site/MediaProbe", { credentials: "include" });
        if (!r.ok) return settle("unknown");
        base = (await r.json())?.mediaBase;
      } catch {
        // The APP itself is unreachable, which is a different and self-evident problem. Say nothing:
        // a banner about media is noise when the page barely loaded.
        return settle("unknown");
      }
      if (!base) return settle("unknown"); // no gateway configured (dev): nothing to warn about

      if ((await probe(`${base}/`, PROBE_TIMEOUT_MS)) !== "yes") return settleUnreachable();

      const v6 = v6ProbeUrl(base);
      if (!v6) return settle("ok");
      // Only an outright rejection means "no IPv6"; a timeout is ambiguity and stays silent.
      settle((await probe(v6, V6_PROBE_TIMEOUT_MS)) === "no" ? "ok-v4" : "ok");
    })();

    return () => { alive = false; if (retry) clearTimeout(retry); };
  }, [state]);

  return state;
}
