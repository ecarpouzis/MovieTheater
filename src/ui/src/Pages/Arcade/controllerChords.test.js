import { describe, it, expect } from "vitest";
import { PAD } from "./cloudRetroClient";
import { DEFAULT_CHORDS, createChordWatcher, resolveChords } from "./controllerChords";

const maskOf = (...names) => names.reduce((m, name) => m | (1 << PAD[name]), 0);

describe("resolveChords — custom binds merged over the defaults", () => {
  it("overrides an action's bits, keeps its default holdMs, and leaves other actions untouched", () => {
    const r = resolveChords({ quickSave: ["SELECT", "B"] });
    const save = r.find((c) => c.action === "quickSave");
    const load = r.find((c) => c.action === "quickLoad");
    expect(save.bits).toEqual(["SELECT", "B"]);
    expect(save.holdMs).toBe(DEFAULT_CHORDS.find((c) => c.action === "quickSave").holdMs);
    expect(load.bits).toEqual(DEFAULT_CHORDS.find((c) => c.action === "quickLoad").bits); // untouched
  });

  it("falls back to the default when the custom entry is empty, missing, or all-invalid", () => {
    expect(resolveChords({}).map((c) => c.bits)).toEqual(DEFAULT_CHORDS.map((c) => c.bits));
    expect(resolveChords({ reset: [] }).find((c) => c.action === "reset").bits)
      .toEqual(DEFAULT_CHORDS.find((c) => c.action === "reset").bits);
    expect(resolveChords({ quickSave: ["NOPE", 42] }).find((c) => c.action === "quickSave").bits)
      .toEqual(DEFAULT_CHORDS.find((c) => c.action === "quickSave").bits);
  });

  it("a resolved custom chord actually fires through the watcher", () => {
    const fired = [];
    const w = createChordWatcher((a) => fired.push(a), resolveChords({ quickSave: ["SELECT", "B"] }));
    w.poll(maskOf("SELECT", "B"), 0);
    w.poll(maskOf("SELECT", "B"), 1000);
    expect(fired).toEqual(["quickSave"]);
  });
});

describe("createChordWatcher — basic hold-to-fire semantics", () => {
  it("does not fire before holdMs, fires exactly once at the threshold while held continuously", () => {
    const fired = [];
    const w = createChordWatcher((a) => fired.push(a), [{ action: "test", bits: ["L3", "R3"], holdMs: 600 }]);
    const mask = maskOf("L3", "R3");

    w.poll(mask, 0);
    w.poll(mask, 300);
    expect(fired).toEqual([]);
    w.poll(mask, 599);
    expect(fired).toEqual([]);
    w.poll(mask, 600);
    expect(fired).toEqual(["test"]);
    w.poll(mask, 601); // still held — must not re-fire
    expect(fired).toEqual(["test"]);
  });

  it("requires the chord's bits to be FULLY released before it can fire again", () => {
    const fired = [];
    const w = createChordWatcher((a) => fired.push(a), [{ action: "test", bits: ["L3", "R3"], holdMs: 100 }]);
    const mask = maskOf("L3", "R3");
    w.poll(mask, 0);
    w.poll(mask, 100);
    expect(fired).toEqual(["test"]);

    // Dropping only ONE of the two bits still counts as "released" (chord no longer fully satisfied).
    w.poll(1 << PAD.L3, 150);
    w.poll(mask, 150);
    w.poll(mask, 250);
    expect(fired).toEqual(["test", "test"]);
  });

  it("superset matching: unrelated extra bits held alongside the chord don't block it", () => {
    const fired = [];
    const w = createChordWatcher((a) => fired.push(a), [{ action: "test", bits: ["L3", "R3"], holdMs: 100 }]);
    const mask = maskOf("L3", "R3", "UP"); // an unrelated bit riding along
    w.poll(mask, 0);
    w.poll(mask, 100);
    expect(fired).toEqual(["test"]);
  });
});

describe("createChordWatcher — DEFAULT_CHORDS subset-collision (quickSave ⊂ quickLoad)", () => {
  it("holding exactly quickSave's bits fires only quickSave", () => {
    const fired = [];
    const w = createChordWatcher((a) => fired.push(a));
    const mask = maskOf("L3", "R3");
    w.poll(mask, 0);
    w.poll(mask, 600);
    w.poll(mask, 900);
    expect(fired).toEqual(["quickSave"]);
  });

  it("holding quickLoad's full bit set fires only quickLoad — quickSave's clock never starts", () => {
    const fired = [];
    const w = createChordWatcher((a) => fired.push(a));
    const mask = maskOf("L3", "R3", "SELECT"); // a strict superset of quickSave's bits
    w.poll(mask, 0);
    w.poll(mask, 600); // quickLoad's own threshold
    expect(fired).toEqual(["quickLoad"]);
    w.poll(mask, 900);
    expect(fired).toEqual(["quickLoad"]); // quickSave never fires, even though its bits are held
  });

  it("adding SELECT mid-hold resets quickSave's progress and lets quickLoad start fresh from that moment", () => {
    const fired = [];
    const w = createChordWatcher((a) => fired.push(a));
    const justSave = maskOf("L3", "R3");
    const withLoad = maskOf("L3", "R3", "SELECT");

    w.poll(justSave, 0);   // quickSave starts building
    w.poll(justSave, 300); // 300ms in, not yet at its 600ms threshold
    expect(fired).toEqual([]);

    w.poll(withLoad, 300); // SELECT added — quickLoad claims L3/R3, quickSave is suppressed+reset
    w.poll(withLoad, 900); // 300 + 600 = quickLoad's threshold from THIS moment
    expect(fired).toEqual(["quickLoad"]);
    // quickSave must NOT fire from its original start (0 + 600 = 600, long past) — suppression
    // voided that progress entirely.
    w.poll(withLoad, 1000);
    expect(fired).toEqual(["quickLoad"]);

    w.poll(justSave, 900);  // SELECT released — quickLoad re-arms, quickSave resumes unsuppressed
    w.poll(justSave, 1499); // one tick before a FRESH 600ms from 900 elapses
    expect(fired).toEqual(["quickLoad"]);
    w.poll(justSave, 1500); // 900 + 600 — quickSave fires only now, proving its clock truly restarted
    expect(fired).toEqual(["quickLoad", "quickSave"]);
  });

  it("DEFAULT_CHORDS exports the three actions this pass ships (fast-forward intentionally absent)", () => {
    expect(DEFAULT_CHORDS.map((c) => c.action).sort()).toEqual(["quickLoad", "quickSave", "reset"]);
  });
});
