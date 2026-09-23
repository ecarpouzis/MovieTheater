import { describe, it, expect, vi, afterEach } from "vitest";
import { shouldSwallowKey, installRoomKeySwallow, installBackTrap, BACK_TRAP_FLAG } from "./roomInputGuard";

const ev = (over = {}) => ({ key: "a", code: "KeyA", target: { tagName: "DIV" }, ...over });

describe("shouldSwallowKey", () => {
  it("swallows plain keys while a pad is connected (A→Enter, d-pad→arrows, Start→Enter)", () => {
    expect(shouldSwallowKey(ev({ key: "Enter", code: "Enter" }), { padConnected: true })).toBe(true);
    expect(shouldSwallowKey(ev({ key: "ArrowDown", code: "ArrowDown" }), { padConnected: true })).toBe(true);
    expect(shouldSwallowKey(ev({ key: " ", code: "Space" }), { padConnected: true })).toBe(true);
    expect(shouldSwallowKey(ev({ key: "Tab", code: "Tab" }), { padConnected: true })).toBe(true);
  });
  it("leaves plain keyboard keys alone when no pad is connected", () => {
    expect(shouldSwallowKey(ev({ key: "Enter", code: "Enter" }), { padConnected: false })).toBe(false);
    expect(shouldSwallowKey(ev({ key: "Tab", code: "Tab" }), { padConnected: false })).toBe(false);
  });
  it("swallows pad-shaped events even before the pad is enumerated (no physical code / GoBack)", () => {
    expect(shouldSwallowKey(ev({ key: "Unidentified", code: "" }), { padConnected: false })).toBe(true);
    expect(shouldSwallowKey(ev({ key: "GoBack", code: "" }), { padConnected: false })).toBe(true);
    expect(shouldSwallowKey(ev({ key: "Enter", code: "" }), { padConnected: false })).toBe(true);
  });
  it("never swallows typing, modifier chords, the function row or Escape", () => {
    expect(shouldSwallowKey(ev({ key: "Enter", code: "Enter", target: { tagName: "INPUT" } }), { padConnected: true })).toBe(false);
    expect(shouldSwallowKey(ev({ key: "x", code: "", target: { tagName: "TEXTAREA" } }), { padConnected: true })).toBe(false);
    expect(shouldSwallowKey(ev({ key: "b", code: "KeyB", target: { isContentEditable: true } }), { padConnected: true })).toBe(false);
    expect(shouldSwallowKey(ev({ key: "r", code: "KeyR", ctrlKey: true }), { padConnected: true })).toBe(false);
    expect(shouldSwallowKey(ev({ key: "F9", code: "F9" }), { padConnected: true })).toBe(false);
    expect(shouldSwallowKey(ev({ key: "F11", code: "F11" }), { padConnected: true })).toBe(false);
    expect(shouldSwallowKey(ev({ key: "Escape", code: "Escape" }), { padConnected: true })).toBe(false);
  });
});

describe("installRoomKeySwallow", () => {
  let off;
  afterEach(() => { off?.(); off = null; });
  it("preventDefaults keydown and keyup on window in the capture phase, and uninstalls", () => {
    off = installRoomKeySwallow({ padConnected: () => true });
    const down = new KeyboardEvent("keydown", { key: "Enter", code: "Enter", cancelable: true, bubbles: true });
    document.body.dispatchEvent(down);
    expect(down.defaultPrevented).toBe(true);
    const up = new KeyboardEvent("keyup", { key: " ", code: "Space", cancelable: true, bubbles: true });
    document.body.dispatchEvent(up);
    expect(up.defaultPrevented).toBe(true);
    off(); off = null;
    const after = new KeyboardEvent("keydown", { key: "Enter", code: "Enter", cancelable: true, bubbles: true });
    document.body.dispatchEvent(after);
    expect(after.defaultPrevented).toBe(false);
  });
});

describe("installBackTrap", () => {
  let off;
  afterEach(() => { off?.(); off = null; window.history.replaceState(null, "", window.location.href); });
  it("pushes a sentinel carrying the router's state, re-pushes on popstate, and reports the trap", () => {
    window.history.replaceState({ key: "r1", state: { descriptor: { code: "ABCD" } } }, "", "/arcade/room/ABCD");
    const onTrapped = vi.fn();
    off = installBackTrap({ onTrapped });
    expect(window.history.state[BACK_TRAP_FLAG]).toBe(true);
    expect(window.history.state.state.descriptor.code).toBe("ABCD");
    // Simulate the browser landing on the entry beneath the sentinel.
    window.history.replaceState({ key: "r1", state: { descriptor: { code: "ABCD" } } }, "", "/arcade/room/ABCD");
    window.dispatchEvent(new PopStateEvent("popstate", { state: window.history.state }));
    expect(window.history.state[BACK_TRAP_FLAG]).toBe(true);
    expect(onTrapped).toHaveBeenCalledTimes(1);
  });
  it("does not stack a second sentinel when mounted on one already", () => {
    window.history.replaceState({ [BACK_TRAP_FLAG]: true }, "", "/arcade/room/ABCD");
    const push = vi.spyOn(window.history, "pushState");
    off = installBackTrap({});
    expect(push).not.toHaveBeenCalled();
    push.mockRestore();
  });
});
