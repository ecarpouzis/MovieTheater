import { describe, it, expect } from "vitest";
import { groupByDecade, jumpCursorFor } from "./PhotoYearRail";

// The rail's two pieces of actual logic, asserted directly. The component around them is layout;
// these are the parts with a right answer per input.

describe("jumpCursorFor", () => {
  it("seeds the cursor strictly before Jan 1 of the following year", () => {
    // id 0 makes the server's tie-break predicate (TakenAt == cursor && Id < 0) empty, so the seek
    // is cleanly strictly-before — nothing stamped exactly midnight New Year leaks into the wrong year.
    expect(jumpCursorFor(2011)).toEqual({ takenAt: "2012-01-01T00:00:00", id: 0 });
  });
});

describe("groupByDecade", () => {
  it("groups consecutive years into their decades, preserving newest-first order", () => {
    const groups = groupByDecade([
      { year: 2024, count: 1 },
      { year: 2020, count: 2 },
      { year: 2019, count: 3 },
      { year: 1997, count: 4 },
      { year: 1991, count: 5 },
    ]);
    expect(groups.map((g) => g.decade)).toEqual([2020, 2010, 1990]);
    expect(groups[0].years.map((y) => y.year)).toEqual([2024, 2020]);
    expect(groups[2].years.map((y) => y.year)).toEqual([1997, 1991]);
  });

  it("handles an empty or missing index", () => {
    expect(groupByDecade([])).toEqual([]);
    expect(groupByDecade(undefined)).toEqual([]);
  });
});
