import { render, screen, cleanup } from "@testing-library/react";
import { describe, it, expect, afterEach } from "vitest";
import { vi } from "vitest";

import MusicSongRow from "./MusicSongRow";
import { orderTracks } from "./MusicAlbumModal";

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
  it("draws the value as width, so a tracklist can be read down the column", () => {
    const { container } = render(<MusicSongRow no="1" title="Creep" popularity={96} onPlay={vi.fn()} />);
    expect(container.querySelector(".music-song-pop-fill").style.width).toBe("96%");
  });

  it("says what the number MEANS, because a bare score would read as a rating", () => {
    render(<MusicSongRow no="1" title="Creep" popularity={96} onPlay={vi.fn()} />);
    expect(screen.getByLabelText("Popularity 96 of 100")).toBeTruthy();
  });

  it("shows nothing at all when popularity is unknown - an empty column, never a zero bar", () => {
    const { container } = render(<MusicSongRow no="1" title="Deep Cut" onPlay={vi.fn()} />);
    expect(container.querySelector(".music-song-pop")).toBeNull();
  });

  it("shows a real ZERO, which is a fact and not a missing value", () => {
    const { container } = render(<MusicSongRow no="1" title="Silence" popularity={0} onPlay={vi.fn()} />);
    expect(container.querySelector(".music-song-pop-fill").style.width).toBe("0%");
  });

  it("clamps a value outside the scale rather than drawing past the trough", () => {
    const { container } = render(<MusicSongRow no="1" title="Odd" popularity={140} onPlay={vi.fn()} />);
    expect(container.querySelector(".music-song-pop-fill").style.width).toBe("100%");
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
