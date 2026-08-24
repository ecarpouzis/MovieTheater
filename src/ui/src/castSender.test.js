import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";

import { loadCastFramework, resetCastFrameworkForTests } from "./castSender";

// The loader is the part of the sender that runs on EVERY browser, including the ones that can never
// cast — so what it must not do (fetch a script that can't work, hang, throw) is worth pinning.

describe("loadCastFramework", () => {
  const originalChrome = window.chrome;
  const originalCast = window.cast;

  beforeEach(() => {
    resetCastFrameworkForTests();
    delete window.cast;
    delete window.chrome;
    delete window.__onGCastApiAvailable;
  });

  afterEach(() => {
    vi.restoreAllMocks();
    if (originalChrome === undefined) delete window.chrome;
    else window.chrome = originalChrome;
    if (originalCast === undefined) delete window.cast;
    else window.cast = originalCast;
  });

  it("resolves false without injecting a script on a non-Chromium browser", async () => {
    // Firefox, Safari and every iOS browser land here. The point is the absent appendChild: loading
    // 100 kB of SDK that cannot initialize, then waiting out the timeout, is pure cost.
    const append = vi.spyOn(document.head, "appendChild");
    await expect(loadCastFramework()).resolves.toBe(false);
    expect(append).not.toHaveBeenCalled();
  });

  it("resolves true immediately when the framework is already present", async () => {
    window.cast = { framework: {} };
    window.chrome = { cast: {} };
    const append = vi.spyOn(document.head, "appendChild");
    await expect(loadCastFramework()).resolves.toBe(true);
    expect(append).not.toHaveBeenCalled();
  });

  it("caches its answer so a remount doesn't re-inject the SDK", async () => {
    window.cast = { framework: {} };
    window.chrome = { cast: {} };
    const first = loadCastFramework();
    expect(loadCastFramework()).toBe(first);
    await first;
  });

  it("resolves false rather than rejecting when the script tag is refused", async () => {
    // A CSP without gstatic throws synchronously from appendChild. Unhandled, that rejection would
    // strand every caller's .then and leave the player half-initialized instead of cast-less.
    window.chrome = {};
    vi.spyOn(document.head, "appendChild").mockImplementation(() => {
      throw new Error("Refused to load the script (CSP).");
    });
    await expect(loadCastFramework()).resolves.toBe(false);
  });

  it("resolves false when the SDK reports itself unavailable", async () => {
    window.chrome = {};
    vi.spyOn(document.head, "appendChild").mockImplementation(() => undefined);
    const pending = loadCastFramework();
    window.__onGCastApiAvailable(false, "no receivers");
    await expect(pending).resolves.toBe(false);
  });

  it("resolves false when the SDK claims availability but left no framework behind", async () => {
    window.chrome = {};
    vi.spyOn(document.head, "appendChild").mockImplementation(() => undefined);
    const pending = loadCastFramework();
    window.__onGCastApiAvailable(true); // no window.cast.framework — nothing usable
    await expect(pending).resolves.toBe(false);
  });

  it("resolves true once the SDK announces a usable framework", async () => {
    window.chrome = {};
    vi.spyOn(document.head, "appendChild").mockImplementation(() => undefined);
    const pending = loadCastFramework();
    window.cast = { framework: {} };
    window.__onGCastApiAvailable(true);
    await expect(pending).resolves.toBe(true);
  });

  it("chains an existing __onGCastApiAvailable instead of stealing it", async () => {
    // Another script (or a previous mount under hot reload) may own the hook; clobbering it would
    // silently break whoever set it first.
    window.chrome = {};
    const previous = vi.fn();
    window.__onGCastApiAvailable = previous;
    vi.spyOn(document.head, "appendChild").mockImplementation(() => undefined);
    const pending = loadCastFramework();
    window.cast = { framework: {} };
    window.__onGCastApiAvailable(true, "ok");
    await expect(pending).resolves.toBe(true);
    expect(previous).toHaveBeenCalledWith(true, "ok");
  });

  it("survives a previous hook that throws", async () => {
    window.chrome = {};
    window.__onGCastApiAvailable = () => {
      throw new Error("not ours to fix");
    };
    vi.spyOn(document.head, "appendChild").mockImplementation(() => undefined);
    const pending = loadCastFramework();
    window.cast = { framework: {} };
    window.__onGCastApiAvailable(true);
    await expect(pending).resolves.toBe(true);
  });

  it("ignores a second announcement after it has already settled", async () => {
    window.chrome = {};
    vi.spyOn(document.head, "appendChild").mockImplementation(() => undefined);
    const pending = loadCastFramework();
    window.cast = { framework: {} };
    window.__onGCastApiAvailable(true);
    window.__onGCastApiAvailable(false);
    await expect(pending).resolves.toBe(true);
  });
});
