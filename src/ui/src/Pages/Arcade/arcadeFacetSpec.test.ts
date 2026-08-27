import { describe, expect, it } from "vitest";
import { parseFacetState } from "../../catalog/rail/facetUrl";
import { ARCADE_PARSE_SPEC, arcadeFacetRows, arcadeFilterParams, arcadeNarrows, legacyToArcadeSearch } from "./arcadeFacetSpec";
import { parseSystems } from "./arcadeSystemFilter";

const state = (search: string) => parseFacetState(search, ARCADE_PARSE_SPEC);

describe("arcadeFilterParams — the rail's URL in the API's vocabulary", () => {
  it("systems join to a csv, hidden regions come from the EXCLUDES, single-valued facets take their last value, q is search", () => {
    const f = arcadeFilterParams(state("?f=system:SNES&f=system:genesis&x=region:Japan&x=region:Europe&f=players:2&f=players:4&f=genre:RPG&f=variant:modded&f=ra:speedruns&q=mario"), "rating");
    expect(f).toEqual({ system: "snes,genesis", hideRegions: "Japan,Europe", maxPlayers: "4", variant: "modded", genre: "RPG", sort: "rating", search: "mario", ra: "speedruns" });
    expect(arcadeFilterParams(state(""), "")).toEqual({ system: "", hideRegions: "", maxPlayers: "", variant: "", genre: "", sort: "", search: "", ra: "" });
    expect(arcadeNarrows(arcadeFilterParams(state(""), ""))).toBe(false);
    expect(arcadeNarrows(arcadeFilterParams(state("?x=region:Japan"), ""))).toBe(true);
  });

  it("the option rows: systems by label, regions/genres by count, RA from its counters, players/variant static", () => {
    const rows = arcadeFacetRows({ systems: [{ value: "snes", count: 40 }], regions: [{ value: "Japan", count: 5 }], genres: [{ value: "RPG", count: 9 }], ra: { achievements: 3, highScores: 2, speedruns: 1 } });
    expect(rows.system).toEqual([{ value: "snes", label: "SNES", count: 40 }]);
    expect(rows.region).toEqual([{ value: "Japan", label: "Japan", count: 5 }]);
    expect(rows.genre[0].count).toBe(9);
    expect(rows.ra.map((r) => r.count)).toEqual([3, 2, 1]);
    expect(rows.players.map((r) => r.label)).toEqual(["2+", "3+", "4+", "5"]);
    expect(rows.variant.map((r) => r.value)).toEqual(["release", "modded", "romhacks"]);
  });
});

describe("legacyToArcadeSearch", () => {
  it("rewrites the old lobby params once, drops variant=all, keeps sort/view/q, and is null for a final URL", () => {
    expect(legacyToArcadeSearch("?f=system%3Anes&q=x&view=wall")).toBeNull();
    expect(legacyToArcadeSearch("?system=snes,Genesis&hideRegions=Japan&players=2&variant=modded&genre=RPG&ra=achievements&q=mario&sort=rating"))
      // The `f=` order follows ARCADE_FACET_DEFS, which the canvas re-ordered to Genre · Players · Region.
      .toBe("?sort=rating&q=mario&f=system%3Asnes&f=system%3Agenesis&f=genre%3ARPG&f=players%3A2&f=variant%3Amodded&f=ra%3Aachievements&x=region%3AJapan");
    expect(legacyToArcadeSearch("?variant=all")).toBe("");
    expect(legacyToArcadeSearch("?system=")).toBe("");
  });

  it("collapses a single-valued facet carrying two pills to its last", () => {
    expect(legacyToArcadeSearch("?f=genre:RPG&f=genre:Platformer")).toBe("?f=genre%3APlatformer");
    expect(legacyToArcadeSearch("?f=system:nes&f=system:snes")).toBeNull();
  });
});

describe("parseSystems", () => {
  it("reads the rail's f=system: entries, still reads the pre-S2c csv, dedupes and lowercases", () => {
    expect(parseSystems("?f=system:SNES&f=system:genesis&f=genre:RPG")).toEqual(["snes", "genesis"]);
    expect(parseSystems("?system=nes,snes&f=system:snes")).toEqual(["snes", "nes"]);
    expect(parseSystems("")).toEqual([]);
  });
});
