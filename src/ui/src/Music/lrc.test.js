import { describe, it, expect } from "vitest";
import { parseLrc, activeLineIndex } from "./lrc";

describe("parseLrc", () => {
  it("parses [mm:ss.xx] cues in order", () => {
    const lines = parseLrc("[00:12.50]First\n[01:05.00]Second\n[00:30.00]Middle");
    expect(lines.map((l) => l.text)).toEqual(["First", "Middle", "Second"]);
    expect(lines[0].time).toBeCloseTo(12.5);
    expect(lines[1].time).toBeCloseTo(30);
    expect(lines[2].time).toBeCloseTo(65);
  });

  it("accepts cues without a fractional part and with a colon separator", () => {
    const lines = parseLrc("[00:09]Nine\n[00:10:50]TenAndAHalf");
    expect(lines[0].time).toBeCloseTo(9);
    expect(lines[1].time).toBeCloseTo(10.5);
  });

  it("expands a line carrying several timestamps (repeated chorus)", () => {
    const lines = parseLrc("[00:10.00][02:40.00]Chorus");
    expect(lines).toHaveLength(2);
    expect(lines.every((l) => l.text === "Chorus")).toBe(true);
    expect(lines[1].time).toBeCloseTo(160);
  });

  it("skips metadata tags and keeps instrumental gaps as blank lines", () => {
    const lines = parseLrc("[ar:Someone]\n[ti:A Song]\n[00:00.00]\n[00:05.00]Words");
    expect(lines).toHaveLength(2);
    expect(lines[0].text).toBe("");
    expect(lines[1].text).toBe("Words");
  });

  it("returns nothing for empty, null or timestamp-free input", () => {
    expect(parseLrc("")).toEqual([]);
    expect(parseLrc(null)).toEqual([]);
    expect(parseLrc("just plain lyrics\nno cues here")).toEqual([]);
  });

  it("treats a bracket after the text as literal, not a cue", () => {
    const lines = parseLrc("[00:01.00]Take me [02:00.00] home");
    expect(lines).toHaveLength(1);
    expect(lines[0].text).toBe("Take me [02:00.00] home");
  });
});

describe("activeLineIndex", () => {
  const lines = parseLrc("[00:00.00]Zero\n[00:10.00]Ten\n[00:20.00]Twenty");

  it("is -1 before the first cue", () => {
    expect(activeLineIndex(parseLrc("[00:05.00]Later"), 1)).toBe(-1);
  });

  it("picks the last cue that has passed", () => {
    expect(activeLineIndex(lines, 0)).toBe(0);
    expect(activeLineIndex(lines, 9.99)).toBe(0);
    expect(activeLineIndex(lines, 10)).toBe(1);
    expect(activeLineIndex(lines, 19.5)).toBe(1);
    expect(activeLineIndex(lines, 250)).toBe(2);
  });

  it("is -1 for empty input or a non-finite time", () => {
    expect(activeLineIndex([], 5)).toBe(-1);
    expect(activeLineIndex(lines, NaN)).toBe(-1);
  });
});
