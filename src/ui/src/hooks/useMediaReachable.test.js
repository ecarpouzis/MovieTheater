import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import useMediaReachable from "./useMediaReachable";

// The media-reach verdicts. The one this file exists for is "blocked-local": on the home network the media
// host's name resolves to a PRIVATE address, and a browser's Local Network Access protection refuses it from
// the public site — while the same host's public IPv6 name answers. That used to read as "unreachable"
// ("nothing will play"), sending people to fix a network that was fine.

const BASE = "https://stream.example.com";
const V6 = "https://mediav6.example.com/";

function stubFetch({ media, v6 }) {
  vi.stubGlobal("fetch", vi.fn(async (url) => {
    if (url === "/API/Site/MediaProbe") return { ok: true, json: async () => ({ mediaBase: BASE }) };
    const outcome = url === V6 ? v6 : media;
    if (outcome === "yes") return { ok: true };
    throw new TypeError("Failed to fetch");
  }));
}

beforeEach(() => { window.sessionStorage.clear(); });
afterEach(() => { vi.unstubAllGlobals(); });

describe("useMediaReachable", () => {
  it("is ok when both names answer", async () => {
    stubFetch({ media: "yes", v6: "yes" });
    const { result } = renderHook(() => useMediaReachable());
    await waitFor(() => expect(result.current).toBe("ok"));
  });

  it("is ok-v4 when only the media name answers", async () => {
    stubFetch({ media: "yes", v6: "no" });
    const { result } = renderHook(() => useMediaReachable());
    await waitFor(() => expect(result.current).toBe("ok-v4"));
  });

  it("is blocked-local when the media name is refused but the same host's public name answers", async () => {
    stubFetch({ media: "no", v6: "yes" });
    const { result } = renderHook(() => useMediaReachable());
    await waitFor(() => expect(result.current).toBe("blocked-local"));
    expect(window.sessionStorage.getItem("mediaReachable")).toBeNull(); // re-probed, never cached
  });

  it("is unreachable only when neither name answers", async () => {
    stubFetch({ media: "no", v6: "no" });
    const { result } = renderHook(() => useMediaReachable());
    await waitFor(() => expect(result.current).toBe("unreachable"));
  });
});
