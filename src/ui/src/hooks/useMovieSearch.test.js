import { renderHook, act } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";

import { useMovieSearch, loadSort, saveSort, BROWSE_SORTS, DEFAULT_SORT } from "./useMovieSearch";

// ── What a browse search states about itself ───────────────────────────────────────────────────
// Two things moved into this hook and are worth pinning:
//
// 1. Random is a SORT, not a special landing mode. It used to be a separate search (a bare `seed`
//    with no sort, on its own code path) which is why the persisted sort didn't apply to the page
//    the site opens on. Now it is one of six values, it is the default, and it carries the seed —
//    without which each infinite-scroll page would reshuffle independently and dupe/skip cards.
//
// 2. A search names its OWN letter source. Browse used to reverse-engineer one by parsing the browse
//    URL, which is what pinned the A–Z strip to the unfiltered Type-scope browse; now every mode that
//    can be bucketed emits a lettersUrl carrying the SAME filter, so the strip follows the
//    alphabetical sort into a genre or person browse too.

const paramsOf = (url) => new URLSearchParams(url.slice(url.indexOf("?")));

beforeEach(() => {
  window.localStorage.clear();
});

describe("the browse sort", () => {
  it("defaults to random — the site's discovery grid is an ordinary sort now", () => {
    expect(DEFAULT_SORT).toBe("random");
    expect(loadSort()).toBe("random");
    expect(BROWSE_SORTS).toContain("random");
  });

  it("persists a chosen sort and rejects a junk one back to the default", () => {
    saveSort("alpha");
    expect(loadSort()).toBe("alpha");
    saveSort("not-a-sort");
    expect(loadSort()).toBe(DEFAULT_SORT);
  });

  it("sends a stable seed with the random sort, and none with any other", () => {
    const { result } = renderHook(() => useMovieSearch());

    act(() => result.current.titleTypeSearch(["Movies"], "random"));
    const first = paramsOf(result.current.search.url);
    expect(first.get("sort")).toBe("random");
    const seed = first.get("seed");
    expect(Number(seed)).toBeGreaterThan(0);

    // Same page load → same seed, so page 2 of the shuffle agrees with page 1.
    act(() => result.current.genreSearch(["Horror"], ["Movies"], "random"));
    expect(paramsOf(result.current.search.url).get("seed")).toBe(seed);

    act(() => result.current.titleTypeSearch(["Movies"], "alpha"));
    const alpha = paramsOf(result.current.search.url);
    expect(alpha.get("sort")).toBe("alpha");
    expect(alpha.get("seed")).toBeNull();
  });
});

describe("the letter strip's source", () => {
  it("follows the alphabetical sort into a filtered browse, carrying that filter", () => {
    const { result } = renderHook(() => useMovieSearch());

    act(() => result.current.genreSearch(["Horror", "Comedy"], ["Movies", "Series"], "alpha"));
    const letters = paramsOf(result.current.search.lettersUrl);
    expect(result.current.search.lettersUrl.startsWith("/API/BrowseLetters")).toBe(true);
    expect(letters.get("mode")).toBe("genre");
    expect(letters.get("value")).toBe("Horror,Comedy");
    expect(letters.get("type")).toBe("Movies,Series");

    act(() => result.current.actorSearch("Ripley", ["Movies"], "alpha"));
    const person = paramsOf(result.current.search.lettersUrl);
    expect(person.get("mode")).toBe("actor");
    expect(person.get("value")).toBe("Ripley");
  });

  it("names no mode for the plain Type-scope browse — that IS the unfiltered bucket walk", () => {
    const { result } = renderHook(() => useMovieSearch());
    act(() => result.current.titleTypeSearch(["Movies"], "alpha"));
    const letters = paramsOf(result.current.search.lettersUrl);
    expect(letters.get("type")).toBe("Movies");
    expect(letters.get("mode")).toBeNull();
  });

  it("offers no letters under any other sort, nor for a Misc-inclusive scope", () => {
    const { result } = renderHook(() => useMovieSearch());

    act(() => result.current.titleTypeSearch(["Movies"], "random"));
    expect(result.current.search.lettersUrl).toBeUndefined();

    act(() => result.current.titleTypeSearch(["Movies"], "imdb"));
    expect(result.current.search.lettersUrl).toBeUndefined();

    // Misc is a curated in-memory merge server-side — there is no DB row order to bucket.
    act(() => result.current.titleTypeSearch(["Movies", "Misc"], "alpha"));
    expect(result.current.search.lettersUrl).toBeUndefined();
  });
});
