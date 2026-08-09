import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import { vi, describe, it, expect, afterEach } from "vitest";

import MusicSongRow from "./MusicSongRow";

afterEach(cleanup);

describe("MusicSongRow trailing actions", () => {
  it("offers a queue button and a playlist button as separate controls", () => {
    render(<MusicSongRow no="1" title="Gooey" onPlay={vi.fn()} onQueue={vi.fn()} onAdd={vi.fn()} />);
    expect(screen.getByLabelText("Add to queue")).toBeTruthy();
    expect(screen.getByLabelText("Add to playlist")).toBeTruthy();
  });

  it("adds just this song to the queue, without touching the playlist picker", () => {
    const onQueue = vi.fn();
    const onAdd = vi.fn();
    render(<MusicSongRow no="1" title="Gooey" onPlay={vi.fn()} onQueue={onQueue} onAdd={onAdd} />);
    fireEvent.click(screen.getByLabelText("Add to queue"));
    expect(onQueue).toHaveBeenCalledTimes(1);
    expect(onAdd).not.toHaveBeenCalled();
  });

  it("does not start playback - queueing is a 'later' action", () => {
    const onPlay = vi.fn();
    render(<MusicSongRow no="1" title="Gooey" onPlay={onPlay} onQueue={vi.fn()} onAdd={vi.fn()} />);
    fireEvent.click(screen.getByLabelText("Add to queue"));
    expect(onPlay).not.toHaveBeenCalled();
  });

  it("hides the queue button when the list doesn't offer queueing", () => {
    render(<MusicSongRow no="1" title="Gooey" onPlay={vi.fn()} onAdd={vi.fn()} />);
    expect(screen.queryByLabelText("Add to queue")).toBeNull();
    expect(screen.getByLabelText("Add to playlist")).toBeTruthy();
  });

  it("disables queueing for an unplayable row, so the click can't be silently dropped", () => {
    const onQueue = vi.fn();
    render(<MusicSongRow no="1" title="Gooey" disabled onPlay={vi.fn()} onQueue={onQueue} onAdd={vi.fn()} />);
    const btn = screen.getByLabelText("Add to queue");
    expect(btn.disabled).toBe(true);
    fireEvent.click(btn);
    expect(onQueue).not.toHaveBeenCalled();
  });
});
