import { describe, expect, it } from "vitest";
import { parseFacetState } from "../../catalog/rail/facetUrl";
import { PHOTOS_PARSE_SPEC, photosFilterParams } from "./photosFacetSpec";

const state = (search: string) => parseFacetState(search, PHOTOS_PARSE_SPEC);

describe("photosFilterParams — the rail's URL in /API/Photos/Browse*'s vocabulary", () => {
  it("maps includes, excludes, the single-valued kind, the year range and q; empty state → empty string", () => {
    const p = new URLSearchParams(photosFilterParams(state("?f=album:summer-2019&x=album:scans&f=person:4&x=person:9&f=kind:photo&f=kind:video&f=camera:iPhone%2012&x=camera:Scanner&y=2015-2019&q=beach")));
    expect(p.getAll("album")).toEqual(["summer-2019"]);
    expect(p.getAll("exAlbum")).toEqual(["scans"]);
    expect(p.getAll("person")).toEqual(["4"]);
    expect(p.getAll("exPerson")).toEqual(["9"]);
    expect(p.get("kind")).toBe("video");
    expect(p.getAll("camera")).toEqual(["iPhone 12"]);
    expect(p.getAll("exCamera")).toEqual(["Scanner"]);
    expect(p.get("yearMin")).toBe("2015");
    expect(p.get("yearMax")).toBe("2019");
    expect(p.get("q")).toBe("beach");
    expect(photosFilterParams(state(""))).toBe("");
    expect(photosFilterParams(state("?f=kind:bogus"))).toBe("");
  });
});
