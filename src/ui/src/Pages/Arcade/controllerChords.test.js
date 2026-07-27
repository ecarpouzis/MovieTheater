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

  it("a rebound hold-type chord keeps its hold semantics", () => {
    const rw = resolveChords({ rewind: ["L3", "B"] }).find((c) => c.action === "rewind");
    expect(rw.bits).toEqual(["L3", "B"]);
    expect(rw.hold).toBe(true);
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

  it("reports a fired chord's bits so the input pump can strip them from the wire", () => {
    const w = createChordWatcher(() => {}, [{ action: "test", bits: ["L3", "R3"], holdMs: 100 }]);
    const mask = maskOf("L3", "R3", "UP");
    expect(w.poll(mask, 0)).toBe(0);        // not fired yet — bits still belong to the game
    expect(w.poll(mask, 100)).toBe(maskOf("L3", "R3")); // fired — strip the chord, keep UP
    expect(w.poll(1 << PAD.UP, 150)).toBe(0); // released — nothing stripped
  });
});

describe("createChordWatcher — hold-type chords (rewind/fast-forward)", () => {
  const holdChord = [{ action: "held", bits: ["SELECT", "Y"], holdMs: 150, hold: true }];

  it("engages after holdMs and releases the moment the combo breaks", () => {
    const events = [];
    const w = createChordWatcher((a, on) => events.push([a, on]), holdChord);
    const mask = maskOf("SELECT", "Y");
    w.poll(mask, 0);
    w.poll(mask, 100);
    expect(events).toEqual([]);
    w.poll(mask, 150);
    expect(events).toEqual([["held", true]]);
    w.poll(mask, 500); // still held — engaged once, no repeats
    expect(events).toEqual([["held", true]]);
    w.poll(1 << PAD.SELECT, 600); // Y released — combo broken
    expect(events).toEqual([["held", true], ["held", false]]);
  });

  it("can re-engage after a release", () => {
    const events = [];
    const w = createChordWatcher((a, on) => events.push([a, on]), holdChord);
    const mask = maskOf("SELECT", "Y");
    w.poll(mask, 0);
    w.poll(mask, 150);
    w.poll(0, 200);
    w.poll(mask, 300);
    w.poll(mask, 450);
    expect(events).toEqual([["held", true], ["held", false], ["held", true]]);
  });

  it("an engaged hold chord reports its bits for stripping only while engaged", () => {
    const w = createChordWatcher(() => {}, holdChord);
    const mask = maskOf("SELECT", "Y", "UP");
    expect(w.poll(mask, 0)).toBe(0);
    expect(w.poll(mask, 150)).toBe(maskOf("SELECT", "Y"));
    expect(w.poll(1 << PAD.UP, 200)).toBe(0);
  });

  it("suppression by a bigger satisfied chord releases an engaged hold chord", () => {
    const events = [];
    const w = createChordWatcher((a, on) => events.push([a, on]), [
      ...holdChord,
      { action: "bigger", bits: ["SELECT", "Y", "START"], holdMs: 300 },
    ]);
    const small = maskOf("SELECT", "Y");
    w.poll(small, 0);
    w.poll(small, 150);
    expect(events).toEqual([["held", true]]);
    w.poll(maskOf("SELECT", "Y", "START"), 200); // bigger chord claims the bits
    expect(events).toEqual([["held", true], ["held", false]]);
  });
});

describe("DEFAULT_CHORDS — the shipped bindings", () => {
  it("ships the five actions with the agreed combos", () => {
    const byAction = Object.fromEntries(DEFAULT_CHORDS.map((c) => [c.action, c]));
    expect(byAction.quickLoad.bits).toEqual(["SELECT", "L3"]);
    expect(byAction.quickSave.bits).toEqual(["SELECT", "R3"]);
    expect(byAction.rewind.bits).toEqual(["SELECT", "Y"]); // west face
    expect(byAction.fastForward.bits).toEqual(["SELECT", "A"]); // east face
    expect(byAction.reset.bits).toEqual(["SELECT", "START", "L2", "R2"]);
    expect(byAction.rewind.hold).toBe(true);
    expect(byAction.fastForward.hold).toBe(true);
    expect(byAction.quickSave.hold).toBeUndefined();
  });

  it("each default combo fires only its own action", () => {
    for (const [bits, want] of [
      [["SELECT", "R3"], ["quickSave"]],
      [["SELECT", "L3"], ["quickLoad"]],
      [["SELECT", "Y"], ["rewind"]],
      [["SELECT", "A"], ["fastForward"]],
      [["SELECT", "START", "L2", "R2"], ["reset"]],
    ]) {
      const fired = [];
      const w = createChordWatcher((a) => fired.push(a));
      const mask = maskOf(...bits);
      w.poll(mask, 0);
      w.poll(mask, 2000);
      expect(fired).toEqual(want);
    }
  });
});
