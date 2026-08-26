import { isSinglePageSpread, loadPageOffset, loadReaderPrefs, loadWebtoonMode, savePageOffset, saveReaderPrefs, saveWebtoonMode, snapToSpreadStart } from "./readerPrefs";
import { loadEpubPrefs, saveEpubPrefs } from "./epubPrefs";

beforeEach(() => window.localStorage.clear());

describe("Books/read/readerPrefs — the standalone's keys and laws", () => {
  it("round-trips under the standalone's key, and a pre-v2 fit is migrated to 'auto'", () => {
    expect(loadReaderPrefs()).toEqual({ fitMode: "auto", splitMode: "none", coverAsPage: false, textZoom: true, webtoonWidth: "normal", webtoonGap: false });
    saveReaderPrefs({ fitMode: "width", splitMode: "r2l", coverAsPage: true, textZoom: false, webtoonWidth: "wide", webtoonGap: true });
    expect(JSON.parse(window.localStorage.getItem("mybooksReaderPrefs")!).v).toBe(2);
    expect(loadReaderPrefs()).toMatchObject({ fitMode: "width", splitMode: "r2l", coverAsPage: true, webtoonWidth: "wide", webtoonGap: true });
    // A v1 record (no version) keeps everything but its fit, which predates the 'auto' default.
    window.localStorage.setItem("mybooksReaderPrefs", JSON.stringify({ fitMode: "height", splitMode: "l2r" }));
    expect(loadReaderPrefs()).toMatchObject({ fitMode: "auto", splitMode: "l2r" });
    window.localStorage.setItem("mybooksReaderPrefs", "{not json");
    expect(loadReaderPrefs().fitMode).toBe("auto");
  });

  it("keeps per-book maps: a zero offset is removed, a webtoon choice is tri-state", () => {
    expect(loadPageOffset(7)).toBe(0);
    savePageOffset(7, 3);
    expect(loadPageOffset(7)).toBe(3);
    expect(JSON.parse(window.localStorage.getItem("mybooksPageOffsets")!)).toEqual({ "7": 3 });
    savePageOffset(7, 0);
    expect(JSON.parse(window.localStorage.getItem("mybooksPageOffsets")!)).toEqual({});
    expect(loadWebtoonMode(7)).toBeNull();
    saveWebtoonMode(7, false);
    expect(loadWebtoonMode(7)).toBe(false);
    expect(JSON.parse(window.localStorage.getItem("mybooksWebtoonModes")!)).toEqual({ "7": false });
  });

  it("spread arithmetic: the cover stands alone, pairs snap to their start", () => {
    expect(snapToSpreadStart(5, "none", true)).toBe(5);
    expect(snapToSpreadStart(0, "l2r", true)).toBe(0);
    expect(snapToSpreadStart(2, "l2r", true)).toBe(1);
    expect(snapToSpreadStart(3, "l2r", true)).toBe(3);
    expect(snapToSpreadStart(3, "l2r", false)).toBe(2);
    expect(isSinglePageSpread(0, "r2l", true)).toBe(true);
    expect(isSinglePageSpread(0, "r2l", false)).toBe(false);
  });

  it("epub prefs clamp and default", () => {
    expect(loadEpubPrefs()).toEqual({ fontScale: 1, fontFamily: "original", theme: "light", lineHeight: 1.5, margin: "normal", columns: 1 });
    saveEpubPrefs({ fontScale: 9, fontFamily: "serif", theme: "sepia", lineHeight: 5, margin: "wide", columns: 2 });
    expect(loadEpubPrefs()).toEqual({ fontScale: 2.2, fontFamily: "serif", theme: "sepia", lineHeight: 2.2, margin: "wide", columns: 2 });
  });
});
