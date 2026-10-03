import { describe, expect, it } from "vitest";
import { runBadge } from "./runStatus";

const r = (status: any, planned: number | null, published: number | null, held: number | null) => ({ status, planned, published, held, basis: null });

describe("runBadge", () => {
  it("hides an unknown or missing status", () => {
    expect(runBadge(null)).toBeNull();
    expect(runBadge(r("Unknown", null, 12, 3))).toBeNull();
  });
  it("labels a finished limited run and a complete holding", () => {
    const b = runBadge(r("Completed", 12, 12, 12))!;
    expect(b.label).toBe("Complete · 12 issues");
    expect(b.held).toBe("Complete run in the library");
    expect(b.complete).toBe(true);
  });
  it("labels a cancellation against its plan and a partial holding", () => {
    const b = runBadge(r("Cancelled", 6, 4, 2))!;
    expect(b.label).toBe("Cancelled at #4 of 6");
    expect(b.held).toBe("Have 2 of 4");
  });
  it("ongoing runs count what came out so far", () => {
    expect(runBadge(r("Ongoing", 5, 3, 3))!.label).toBe("Ongoing · 3 of 5");
    expect(runBadge(r("Ongoing", null, 72, 72))!.held).toBe("Have every issue so far");
  });
  it("ended runs", () => {
    expect(runBadge(r("Ended", null, 298, 296))!.label).toBe("Ended · 298 issues");
  });
});
