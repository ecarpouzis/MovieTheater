import { describe, it, expect, vi } from "vitest";

// The media ELEMENT's decode error walks the engine's staged recovery (recoverMediaError →
// swapAudioCodec + recover → give up) and only a decode that survives all three steps reaches the
// page as { decode: true, width }. Escalation is by COUNT within one instance, not the 3 s window
// hls.js's own errors use: a decoder that rejects the stream does so after chewing on it (the
// 2026-09-20 tablet died 11 s in), so a window would recover forever and the page would never learn.
// That, plus one listener per element however often hls.js re-attaches it, is what keeps a
// transient glitch from teaching a capable browser a false 1080p ceiling (WatchPage.handleFatal).

const { FakeHls } = vi.hoisted(() => {
  class FakeHls {
    static Events = { ERROR: "hlsError", MEDIA_ATTACHED: "hlsMediaAttached", MEDIA_DETACHING: "hlsMediaDetaching", INIT_PTS_FOUND: "hlsInitPtsFound" };
    static ErrorTypes = { NETWORK_ERROR: "networkError", MEDIA_ERROR: "mediaError", OTHER_ERROR: "otherError" };
    static ErrorDetails = { BUFFER_STALLED_ERROR: "bufferStalledError" };
    static isSupported() { return true; }
    constructor() {
      this.handlers = {};
      this.levels = [{ width: 3840 }];
      this.recoverMediaError = vi.fn();
      this.swapAudioCodec = vi.fn();
      this.startLoad = vi.fn();
    }
    on(event, fn) { (this.handlers[event] ||= []).push(fn); }
    trigger(event, data) { (this.handlers[event] || []).forEach((fn) => fn(event, data)); }
  }
  return { FakeHls };
});

vi.mock("hls.js", () => ({ default: FakeHls }));

import { createHls } from "./streamEngine";

function fakeMedia() {
  const target = new EventTarget();
  target.error = null;
  target.videoWidth = 0;
  return target;
}

function decodeError(media) {
  media.error = { code: 3, message: "PipelineStatus::PIPELINE_ERROR_DECODE" };
  media.dispatchEvent(new Event("error"));
}

function attached() {
  const onFatal = vi.fn();
  const hls = createHls({ onFatal });
  const media = fakeMedia();
  hls.trigger(FakeHls.Events.MEDIA_ATTACHED, { media });
  return { hls, media, onFatal };
}

describe("the media element's decode error", () => {
  it("is recovered twice before it is fatal, then reaches the page as a decode failure with the frame width", () => {
    const { hls, media, onFatal } = attached();

    decodeError(media);
    expect(hls.recoverMediaError).toHaveBeenCalledTimes(1);
    expect(onFatal).not.toHaveBeenCalled();

    decodeError(media);
    expect(hls.swapAudioCodec).toHaveBeenCalledTimes(1);
    expect(hls.recoverMediaError).toHaveBeenCalledTimes(2);
    expect(onFatal).not.toHaveBeenCalled();

    decodeError(media);
    expect(onFatal).toHaveBeenCalledTimes(1);
    expect(onFatal.mock.calls[0][0]).toMatchObject({ decode: true, type: "element", width: 3840 });
  });

  it("escalates by count however far apart the errors are — a slow deterministic death still gives up", () => {
    const { hls, media, onFatal } = attached();
    vi.spyOn(performance, "now").mockReturnValueOnce(0).mockReturnValueOnce(20_000).mockReturnValueOnce(40_000);
    decodeError(media);
    decodeError(media);
    decodeError(media);
    expect(hls.recoverMediaError).toHaveBeenCalledTimes(2);
    expect(onFatal).toHaveBeenCalledTimes(1);
    vi.restoreAllMocks();
  });

  it("keeps ONE listener across hls.js's own re-attach (recoverMediaError detaches with an empty payload)", () => {
    const { hls, media, onFatal } = attached();
    // What recoverMediaError() does internally: detach (payload {}), then attach the same element.
    hls.trigger(FakeHls.Events.MEDIA_DETACHING, {});
    hls.trigger(FakeHls.Events.MEDIA_ATTACHED, { media });
    decodeError(media);
    expect(hls.recoverMediaError).toHaveBeenCalledTimes(1); // not 2 — no doubled listener
    expect(onFatal).not.toHaveBeenCalled();
  });

  it("stops listening once the media is detached, even with hls.js's empty detach payload", () => {
    const { hls, media } = attached();
    hls.trigger(FakeHls.Events.MEDIA_DETACHING, {});
    decodeError(media);
    expect(hls.recoverMediaError).not.toHaveBeenCalled();
  });

  it("gives up at once on a non-decode element error, flagged as not a decode failure", () => {
    const { hls, media, onFatal } = attached();
    media.error = { code: 4, message: "MEDIA_ELEMENT_ERROR: Format error" };
    media.dispatchEvent(new Event("error"));
    expect(hls.recoverMediaError).not.toHaveBeenCalled();
    expect(onFatal).toHaveBeenCalledTimes(1);
    expect(onFatal.mock.calls[0][0].decode).toBe(false);
  });
});
