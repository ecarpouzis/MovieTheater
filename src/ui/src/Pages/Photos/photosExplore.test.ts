import {
  PHOTOS_MORE,
  composePhotosExplore,
  onThisDaySubtitle,
  onThisDayTitle,
  photoPersonHref,
  toPersonCard,
  topPeople,
} from "./photosExplore";
import type { PhotoCardRow } from "../../catalog/sources/photosSource";

const photo = (id: number, takenAt = "2015-08-27T10:00:00Z"): PhotoCardRow =>
  ({ id, takenAt, width: 4000, height: 3000, gridUrl: `/p/${id}.webp`, kind: "image" });

describe("Pages/Photos/photosExplore — the Photos Explore composition (R9 S7)", () => {
  it("names its rails and drops the ones with nothing in them", () => {
    const out = composePhotosExplore({
      onThisDay: { items: [photo(1), photo(2)], month: 8, day: 27, years: [2015, 2009] },
      recent: [photo(10)],
      people: [{ id: 4, name: "Grandma", tagCount: 300, coverUrl: "/p/4.webp" }],
    });
    expect(out.rails.map((r) => r.key)).toEqual(["on-this-day", "recent", "people"]);
    expect(out.rails[0].title).toBe("On this day — August 27");
    // Nothing on this page is a roll, so there is no seed to shuffle.
    expect(out.seed).toBeUndefined();

    expect(composePhotosExplore({}).rails).toHaveLength(0);
  });

  it("the hero leads with the anniversary, and falls back to the newest arrivals", () => {
    const anniversary = composePhotosExplore({ onThisDay: { items: [photo(1)], month: 8, day: 27 }, recent: [photo(9)] });
    expect(anniversary.spotlight.map((c) => c.id)).toEqual([1]);
    const plain = composePhotosExplore({ recent: [photo(9), photo(8)] });
    expect(plain.spotlight.map((c) => c.id)).toEqual([9, 8]);
  });

  it("routes a person GROUP card to the browse with the person facet", () => {
    const card = toPersonCard({ id: 12, name: "Grandma", tagCount: 300, coverUrl: "/p/x.webp" })!;
    expect(card.kind).toBe("person");
    expect(card.groupKey).toBe("12");
    expect(card.count).toBe(300);
    expect(photoPersonHref(card.groupKey!)).toBe("/photos/browse?f=person%3A12");
    expect(PHOTOS_MORE.people).toBe("/photos/people");
    // An unnamed cluster is a question, not a shelf.
    expect(toPersonCard({ id: 5, name: "  ", tagCount: 40 })).toBeNull();
  });

  it("the people rail leads with the most-photographed and drops one-offs", () => {
    const rows = topPeople([
      { id: 1, name: "A", tagCount: 5 },
      { id: 2, name: "B", tagCount: 90 },
      { id: 3, name: "C", tagCount: 1 },
      { id: 4, name: "", tagCount: 400 },
    ]);
    expect(rows.map((p) => p.id)).toEqual([2, 1]);
  });

  it("says what the anniversary reaches over, and says nothing when it has nothing to say", () => {
    expect(onThisDaySubtitle([2015, 2009, 2021])).toBe("Across 3 years, 2009–2021");
    expect(onThisDaySubtitle([2015])).toBe("From 2015");
    expect(onThisDaySubtitle([])).toBeUndefined();
    expect(onThisDayTitle(undefined, undefined)).toBe("On this day");
  });
});
