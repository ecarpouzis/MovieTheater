import { afterEach, describe, expect, it, vi } from "vitest";
import { canReceiveCodec, decideAutoCodec, localNetworkPermission, resolveAutoCodec, videoProblemMessage } from "./arcadeRoomCreate";

// Answers are what each engine reported on Ziggy, 2026-10-02 (S = supported, P = powerEfficient).
const ans = (supported, powerEfficient) => ({ supported, smooth: supported, powerEfficient });
function stubNavigator({ av1, h264, mobile = false, ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)", throws = false }) {
  vi.stubGlobal("navigator", {
    userAgent: ua,
    maxTouchPoints: 0,
    userAgentData: mobile === null ? undefined : { mobile },
    mediaCapabilities: {
      decodingInfo: vi.fn(async ({ video }) => {
        if (throws) throw new TypeError("webrtc type unsupported");
        return /AV1/.test(video.contentType) ? av1 : h264;
      }),
    },
  });
}

afterEach(() => { vi.unstubAllGlobals(); });

describe("resolveAutoCodec", () => {
  it("hardware AV1 (Chrome/Edge on a modern GPU) gets av1", async () => {
    stubNavigator({ av1: ans(true, true), h264: ans(true, true) });
    expect(await resolveAutoCodec()).toBe("av1");
  });

  it("desktop Firefox (both software) gets av1 — dav1d keeps up where OpenH264 drowned", async () => {
    stubNavigator({ av1: ans(true, false), h264: ans(true, false), mobile: null, ua: "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:157.0) Gecko/20100101 Firefox/157.0" });
    expect(await resolveAutoCodec()).toBe("av1");
  });

  it("a tablet with hardware H.264 only gets h264", async () => {
    stubNavigator({ av1: ans(true, false), h264: ans(true, true), mobile: true });
    expect(await resolveAutoCodec()).toBe("h264");
  });

  it("a phone with no hardware decoder for either keeps h264", async () => {
    stubNavigator({ av1: ans(true, false), h264: ans(true, false), mobile: null, ua: "Mozilla/5.0 (Android 14; Mobile; rv:157.0) Gecko/157.0 Firefox/157.0" });
    expect(await resolveAutoCodec()).toBe("h264");
  });

  it("an older GPU desktop (hardware H.264, software AV1) gets h264", async () => {
    stubNavigator({ av1: ans(true, false), h264: ans(true, true) });
    expect(await resolveAutoCodec()).toBe("h264");
  });

  it("no AV1 decoder at all (older Safari) gets h264", async () => {
    stubNavigator({ av1: ans(false, false), h264: ans(true, true), mobile: null, ua: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Version/16.6 Safari/605.1.15" });
    expect(await resolveAutoCodec()).toBe("h264");
  });

  it("Firefox without the OpenH264 plugin gets av1", async () => {
    stubNavigator({ av1: ans(true, false), h264: ans(false, false), mobile: true });
    expect(await resolveAutoCodec()).toBe("av1");
  });

  it("a probe failure falls back to the receiver's RTP capabilities", async () => {
    stubNavigator({ throws: true });
    vi.stubGlobal("RTCRtpReceiver", { getCapabilities: () => ({ codecs: [{ mimeType: "video/H264" }, { mimeType: "video/VP8" }] }) });
    expect(await resolveAutoCodec()).toBe("h264");
    vi.stubGlobal("RTCRtpReceiver", { getCapabilities: () => ({ codecs: [{ mimeType: "video/AV1" }, { mimeType: "video/H264" }] }) });
    expect(await resolveAutoCodec()).toBe("av1");
  });

  it("a probe failure with no capability API keeps the av1 default", async () => {
    stubNavigator({ throws: true });
    vi.stubGlobal("RTCRtpReceiver", undefined);
    expect(await resolveAutoCodec()).toBe("av1");
  });
});

describe("decideAutoCodec probe summary", () => {
  it("records every answer, the mobile flag and the decision", async () => {
    stubNavigator({ av1: ans(true, false), h264: ans(true, true), mobile: true });
    expect(await decideAutoCodec()).toEqual({ codec: "h264", probe: "av1:Ss- h264:SsP h265:SsP m1 auto=h264" });
  });

  it("marks a failed probe with '?' and still decides", async () => {
    stubNavigator({ throws: true });
    vi.stubGlobal("RTCRtpReceiver", { getCapabilities: () => ({ codecs: [{ mimeType: "video/AV1" }] }) });
    expect(await decideAutoCodec()).toEqual({ codec: "av1", probe: "av1:? h264:? h265:? m0 auto=av1" });
  });
});

describe("canReceiveCodec", () => {
  it("answers from the RTP receive capabilities", () => {
    vi.stubGlobal("RTCRtpReceiver", { getCapabilities: () => ({ codecs: [{ mimeType: "video/H264" }] }) });
    expect(canReceiveCodec("h264")).toBe(true);
    expect(canReceiveCodec("av1")).toBe(false);
  });

  it("is unknown (null) for a room with no recorded codec or no capability API", () => {
    expect(canReceiveCodec("")).toBeNull();
    vi.stubGlobal("RTCRtpReceiver", undefined);
    expect(canReceiveCodec("av1")).toBeNull();
  });
});

describe("videoProblemMessage", () => {
  it("names the codec and the fix for a codec failure", () => {
    const m = videoProblemMessage({ kind: "codec", codec: "av1" });
    expect(m).toMatch(/can't play AV1/);
    expect(m).toMatch(/Codec: H\.264/);
  });

  it("treats packets-that-never-decode as a codec problem", () => {
    expect(videoProblemMessage({ kind: "not-decoding", codec: "h264" })).toMatch(/can't play H\.264.*Codec: AV1/);
  });

  it("points at the local-network permission when the browser reports it blocked or pending", () => {
    expect(videoProblemMessage({ kind: "no-media", codec: "av1" }, "prompt")).toMatch(/local network access/);
    expect(videoProblemMessage({ kind: "no-media", codec: "av1" }, "denied")).toMatch(/local network access/);
  });

  it("tells a mismatched joiner to rejoin, not to change browsers", () => {
    expect(videoProblemMessage({ kind: "mismatch", codec: "h264" })).toMatch(/join the room again/);
  });

  it("falls back to a generic path message", () => {
    expect(videoProblemMessage({ kind: "no-media" }, "granted")).toMatch(/no picture is reaching this browser/);
  });
});

describe("localNetworkPermission", () => {
  it("returns null when no known permission name is accepted", async () => {
    vi.stubGlobal("navigator", { permissions: { query: vi.fn(async () => { throw new TypeError("bad name"); }) } });
    expect(await localNetworkPermission()).toBeNull();
  });

  it("falls through to the older name", async () => {
    const query = vi.fn(async ({ name }) => { if (name === "local-network-access") throw new TypeError(); return { state: "prompt" }; });
    vi.stubGlobal("navigator", { permissions: { query } });
    expect(await localNetworkPermission()).toBe("prompt");
  });
});
