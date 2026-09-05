import { buildMarksIndex, friendMarksModeOf } from "./friendMarks";

const peers = [
  { userId: 1, username: "Eric", moviesSeen: [10, 11], moviesToWatch: [12] },
  { userId: 2, username: "Alex", moviesSeen: [10], moviesToWatch: [11] },
  { userId: 3, username: "Jamie", moviesSeen: [10], moviesToWatch: [10] },
];

describe("Pages/Browse/friendMarks — the counts the poster pill reads", () => {
  it("indexes everybody but the viewer, seen and wants, per title", () => {
    const idx = buildMarksIndex(peers, "eric", "all");
    expect(idx.get(10)).toEqual({ seen: ["Alex", "Jamie"], want: ["Jamie"] });
    expect(idx.get(11)).toEqual({ seen: [], want: ["Alex"] });
    expect(idx.get(12)).toBeUndefined();
  });

  it("'Wants only' drops the seen half; 'Off' indexes nothing", () => {
    expect(buildMarksIndex(peers, "Eric", "want").get(10)).toEqual({ seen: [], want: ["Jamie"] });
    expect(buildMarksIndex(peers, "Eric", "off").size).toBe(0);
  });

  it("reads the lever with a default", () => {
    expect(friendMarksModeOf({ extras: {} })).toBe("all");
    expect(friendMarksModeOf({ extras: { friendMarks: "want" } })).toBe("want");
    expect(friendMarksModeOf({ extras: { friendMarks: "bogus" } })).toBe("all");
  });
});
