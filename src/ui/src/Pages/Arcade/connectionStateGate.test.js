import { describe, it, expect } from "vitest";
import { createConnectionStateGate, PRECONNECT_SETTLE_MS } from "./connectionStateGate";

// A hand-driven timer so the settle window is explicit.
function harness(initial = "new") {
  let state = initial;
  let pending = null;
  const reports = [];
  const gate = createConnectionStateGate({
    report: (s) => reports.push(s),
    current: () => state,
    setTimer: (fn, ms) => { pending = { fn, ms }; return pending; },
    clearTimer: (t) => { if (pending === t) pending = null; },
  });
  return {
    reports,
    set(s) { state = s; gate.onState(s); },
    fire() { const p = pending; pending = null; if (p) p.fn(); },
    pendingMs: () => (pending ? pending.ms : null),
    gate,
  };
}

describe("createConnectionStateGate", () => {
  it("does not report a failure that flickers before the connection was ever up", () => {
    // The relay-room race: connecting -> failed -> connecting -> failed -> connecting -> connected in ~10 ms.
    const h = harness();
    h.set("connecting");
    h.set("failed");
    h.set("connecting");
    h.set("failed");
    h.set("connecting");
    expect(h.pendingMs()).toBe(PRECONNECT_SETTLE_MS);
    h.set("connected");
    expect(h.pendingMs()).toBe(null); // connected cancels the pending verdict
    h.fire();
    expect(h.reports).toEqual([]);
  });

  it("does not report when the settle window ends with the connection still being made", () => {
    const h = harness();
    h.set("connecting");
    h.set("failed");
    h.set("connecting");
    h.fire();
    expect(h.reports).toEqual([]);
  });

  it("still reports a connection that really cannot be made, once, after the settle window", () => {
    const h = harness();
    h.set("connecting");
    h.set("failed");
    h.set("failed"); // a repeat while the verdict is pending arms nothing new
    expect(h.reports).toEqual([]);
    h.fire();
    expect(h.reports).toEqual(["failed"]);
  });

  it("reports at once after the connection has been up — a worker that dies is a crash", () => {
    const h = harness();
    h.set("connecting");
    h.set("connected");
    h.set("disconnected");
    h.set("failed");
    expect(h.reports).toEqual(["disconnected", "failed"]);
    expect(h.gate.everConnected()).toBe(true);
  });

  it("reports nothing for a connection closed during the settle window", () => {
    const h = harness();
    h.set("failed");
    h.set("closed");
    h.fire();
    expect(h.reports).toEqual([]);
  });
});
