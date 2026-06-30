import { computeScores } from "./computeScores";

const movie = (key) => ({ type: "movie", key });
const anchor = (value) => ({ type: "anchor", value });

describe("computeScores", () => {
  it("returns an empty map for an empty or invalid list", () => {
    expect(computeScores([]).size).toBe(0);
    expect(computeScores(null).size).toBe(0);
    expect(computeScores(undefined).size).toBe(0);
  });

  it("spreads N movies evenly in (0,100) when there are no anchors", () => {
    const s = computeScores([movie("a"), movie("b"), movie("c")]);
    // i-th of N in (0,100): 100*(N+1-i)/(N+1) → 75, 50, 25
    expect(s.get("a")).toBe(75);
    expect(s.get("b")).toBe(50);
    expect(s.get("c")).toBe(25);
  });

  it("lifts the floor when a single anchor=30 sits at the very bottom (worked example)", () => {
    const s = computeScores([movie("a"), movie("b"), movie("c"), anchor(30)]);
    // movies spread in (30,100): 30 + 70*(N+1-i)/(N+1), N=3 → 82.5, 65, 47.5 → 83, 65, 48
    expect(s.get("a")).toBe(83);
    expect(s.get("b")).toBe(65);
    expect(s.get("c")).toBe(48);
    expect(s.get("c")).toBeGreaterThan(30); // lowest movie is "roughly 30", clearly above the anchor
  });

  it("caps the top movie below the value of an anchor placed at the very top", () => {
    const s = computeScores([anchor(80), movie("a"), movie("b")]);
    // movies spread in (0,80): 80*(N+1-i)/(N+1), N=2 → 53.3, 26.7 → 53, 27
    expect(s.get("a")).toBe(53);
    expect(s.get("b")).toBe(27);
    expect(s.get("a")).toBeLessThan(80);
  });

  it("brackets each run independently with multiple anchors", () => {
    const s = computeScores([movie("a"), anchor(70), movie("b"), anchor(40), movie("c")]);
    expect(s.get("a")).toBe(85); // (70,100): 100 - 30/2
    expect(s.get("b")).toBe(55); // (40,70): 70 - 30/2
    expect(s.get("c")).toBe(20); // (0,40): 40 - 40/2
  });

  it("clamps anchor values to be monotonic non-increasing down the list", () => {
    // the second anchor (90) sits below the first (50) and is clamped down to 50
    const s = computeScores([movie("a"), anchor(50), movie("b"), anchor(90), movie("c")]);
    expect(s.get("a")).toBe(75); // (50,100): 100 - 50/2
    expect(s.get("b")).toBe(50); // (50,50): gap 0 → exactly 50
    expect(s.get("c")).toBe(25); // (0,50): 50 - 50/2
  });

  it("handles consecutive anchors with an empty run between them", () => {
    const s = computeScores([movie("a"), anchor(60), anchor(40), movie("b")]);
    expect(s.get("a")).toBe(80); // (60,100): 100 - 40/2
    expect(s.get("b")).toBe(20); // (0,40): 40 - 40/2
  });

  it("assigns the exact value to a movie bracketed by two equal anchors", () => {
    const s = computeScores([anchor(50), movie("a"), anchor(50)]);
    expect(s.get("a")).toBe(50);
  });

  it("keeps every score an integer within [0,100]", () => {
    const items = Array.from({ length: 25 }, (_, i) => movie("m" + i));
    const s = computeScores(items);
    for (const v of s.values()) {
      expect(Number.isInteger(v)).toBe(true);
      expect(v).toBeGreaterThanOrEqual(0);
      expect(v).toBeLessThanOrEqual(100);
    }
  });

  it("produces scores that strictly follow rank order, across anchors", () => {
    const s = computeScores([movie("a"), movie("b"), anchor(40), movie("c"), movie("d")]);
    expect(s.get("a")).toBeGreaterThan(s.get("b"));
    expect(s.get("b")).toBeGreaterThan(s.get("c"));
    expect(s.get("c")).toBeGreaterThan(s.get("d"));
  });
});
