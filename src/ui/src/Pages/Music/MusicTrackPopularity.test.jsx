import { render, screen, cleanup } from "@testing-library/react";
import { describe, it, expect, afterEach } from "vitest";
import { vi } from "vitest";

import MusicSongRow from "./MusicSongRow";
import { orderTracks } from "./MusicAlbumModal";
import { peakOf, shareOf, formatListeners, popularityTitle } from "./musicPopularity";

afterEach(cleanup);

/**
 * Track-level popularity in the UI (2026-08-31): the meter on a song row, and the album sheet's
 * "Most popular" ordering.
 *
 * The ordering has a real bug behind it: the album sheet builds its play queue from the SAME array
 * it renders, so a sort that mutated the album's own track list — or a render that sorted one array
 * while the queue was built from another — would make "play from here" start the wrong song. That is
 * what the identity and mutation assertions below are for.
 */
describe("the popularity meter on a song row", () => {
  // A hit and a deep cut off the same real album: 23 points apart on the 0-100 scale, 39x apart in
  // people. The two channels have to disagree about how big that gap looks, and that is the point.
  const hit = { popularity: 73, listeners: 112303 };
  const deepCut = { popularity: 50, listeners: 2905 };
  const peak = peakOf([hit, deepCut]);

  it("prints the ABSOLUTE score, so two songs can be compared exactly", () => {
    render(<MusicSongRow no="1" title="Like The Weather" popularity={hit} popularityPeak={peak} onPlay={vi.fn()} />);
    expect(screen.getByText("73")).toBeTruthy();
  });

  it("draws the bar from LISTENERS, so a 39x drop does not look like a few pixels", () => {
    const { container } = render(
      <MusicSongRow no="9" title="These Days" popularity={deepCut} popularityPeak={peak} onPlay={vi.fn()} />
    );
    // 2,905 / 112,303 = 2.6%. Drawn from the score it would have been 50/73 = 68% - the flattening
    // this whole module exists to undo.
    const width = parseFloat(container.querySelector(".music-song-pop-fill").style.width);
    expect(width).toBeLessThan(5);
  });

  it("gives the loudest song in the list a full bar - it IS the comparison", () => {
    const { container } = render(<MusicSongRow no="1" title="Like The Weather" popularity={hit} popularityPeak={peak} onPlay={vi.fn()} />);
    expect(container.querySelector(".music-song-pop-fill").style.width).toBe("100%");
  });

  it("keeps a tiny share visible rather than letting it vanish into the trough", () => {
    const nothing = { popularity: 10, listeners: 1 };
    const { container } = render(<MusicSongRow no="2" title="Obscure" popularity={nothing} popularityPeak={peak} onPlay={vi.fn()} />);
    expect(parseFloat(container.querySelector(".music-song-pop-fill").style.width)).toBe(2);
  });

  it("shows nothing at all when popularity is unknown - an empty column, never a zero bar", () => {
    const { container } = render(<MusicSongRow no="1" title="Deep Cut" onPlay={vi.fn()} />);
    expect(container.querySelector(".music-song-pop")).toBeNull();
  });

  it("shows a real ZERO, which is a fact and not a missing value", () => {
    render(<MusicSongRow no="1" title="Silence" popularity={{ popularity: 0 }} popularityPeak={peak} onPlay={vi.fn()} />);
    expect(screen.getByText("0")).toBeTruthy();
  });
});

describe("the comparison arithmetic", () => {
  it("falls back to the SCORE ratio when listener counts are missing", () => {
    // An older shelf, or rows enriched before the counts were banked. It understates the drop, but
    // it never invents one.
    const peak = peakOf([{ popularity: 80 }, { popularity: 40 }]);
    expect(shareOf({ popularity: 40 }, peak)).toBeCloseTo(0.5);
  });

  it("prefers listeners over the score whenever both ends are known", () => {
    const peak = peakOf([{ popularity: 80, listeners: 1000 }, { popularity: 40, listeners: 10 }]);
    expect(shareOf({ popularity: 40, listeners: 10 }, peak)).toBeCloseTo(0.01);
  });

  it("has no peak at all when nothing in the list is known", () => {
    expect(peakOf([{ title: "a" }, { title: "b" }])).toBeNull();
    expect(shareOf({ popularity: 50 }, null)).toBe(0);
  });

  it("shortens listener counts to something that fits a tooltip", () => {
    expect(formatListeners(112303)).toBe("112K");
    expect(formatListeners(4210229)).toBe("4.2M");
    expect(formatListeners(21000000)).toBe("21M");
    expect(formatListeners(842)).toBe("842");
    expect(formatListeners(null)).toBeNull();
  });

  it("names the library ranking and how many services agreed on it", () => {
    // A consensus of one is a single service's opinion wearing a ranking's clothes, and the tooltip
    // is the only place that can say so.
    const peak = peakOf([{ popularity: 73, listeners: 112303 }]);
    const many = popularityTitle({ popularity: 70, listeners: 90000, rank: 97, rankSources: 2 }, peak);
    expect(many).toContain("top 3% of the library (2 sources agree)");
    const one = popularityTitle({ popularity: 70, listeners: 90000, rank: 40, rankSources: 1 }, peak);
    expect(one).toContain("top 60% of the library (1 source)");
  });

  it("says what the number means AND what the bar is a share of", () => {
    const peak = peakOf([{ popularity: 73, listeners: 112303 }]);
    const title = popularityTitle({ popularity: 50, listeners: 2905 }, peak);
    expect(title).toContain("50/100");
    expect(title).toContain("not how good");
    expect(title).toContain("3K listeners");
    expect(title).toContain("% of the most-heard song here");
  });
});

describe("the album sheet's tracklist order", () => {
  const album = [
    { id: 1, trackNo: 1, discNo: 1, title: "Opener", popularity: 40 },
    { id: 2, trackNo: 2, discNo: 1, title: "The Hit", popularity: 91 },
    { id: 3, trackNo: 3, discNo: 1, title: "Deep Cut" },
    { id: 4, trackNo: 4, discNo: 1, title: "Closer", popularity: 62 },
  ];

  it("leaves the running order EXACTLY alone by default - a sequence is authored", () => {
    // Identity, not just equality: the default path must not even copy, or every render of an
    // untouched sheet hands React a new array.
    expect(orderTracks(album, "album")).toBe(album);
  });

  it("puts the well-known songs first when asked", () => {
    expect(orderTracks(album, "popular").map((t) => t.title))
      .toEqual(["The Hit", "Closer", "Opener", "Deep Cut"]);
  });

  it("files an unknown song LAST rather than treating it as a zero", () => {
    // "We have never been told" is not "nobody has heard it" — a track with a real 0 would outrank
    // this one, and does in the assertion below.
    const withZero = [{ id: 5, trackNo: 5, discNo: 1, title: "Truly Unheard", popularity: 0 }, ...album];
    const order = orderTracks(withZero, "popular").map((t) => t.title);
    expect(order.indexOf("Truly Unheard")).toBeLessThan(order.indexOf("Deep Cut"));
    expect(order[order.length - 1]).toBe("Deep Cut");
  });

  it("never mutates the album it was handed, so the queue and the rows cannot drift apart", () => {
    const before = album.map((t) => t.id);
    orderTracks(album, "popular");
    expect(album.map((t) => t.id)).toEqual(before);
  });

  it("breaks ties on the running order, so the album still reads as itself", () => {
    const tied = [
      { id: 1, trackNo: 3, discNo: 1, title: "Third", popularity: 50 },
      { id: 2, trackNo: 1, discNo: 1, title: "First", popularity: 50 },
      { id: 3, trackNo: 2, discNo: 2, title: "Disc two", popularity: 50 },
    ];
    expect(orderTracks(tied, "popular").map((t) => t.title)).toEqual(["First", "Third", "Disc two"]);
  });
});
