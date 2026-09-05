import { programMeta, programHeadline, restartIntent, restartHref, previewEnabledFor, previewCapabilities, PREVIEW_BPS, minutesLeft } from "./guideModel";

vi.mock("../../streamCapabilities", () => ({
  detectStreamCapabilities: () => ({ supportsHevc: true, supportsHdr: true, supportsFmp4: true, supportsMkv: true, maxAudioChannels: 6 }),
}));

const at = (h, m = 0) => new Date(Date.UTC(2030, 0, 1, h, m)).toISOString();

describe("Tv/guideModel — the meta line", () => {
  it("reads a movie as year · certificate · slot length", () => {
    const prog = { title: "My Big Fat Greek Wedding", year: 2002, rating: "PG", startUtc: at(9, 20), endUtc: at(10, 55) };
    expect(programMeta(prog)).toBe("2002 · PG · 1h 35m");
    expect(programHeadline(prog)).toBe("My Big Fat Greek Wedding");
  });

  it("reads an episode as S/E · episode title · certificate · length, headlined by the series", () => {
    const prog = { title: "George Lopez – S03E09 Fishing Cubans", seriesTitle: "George Lopez", episodeTitle: "Fishing Cubans", season: 3, episode: 9, rating: "TV-PG", startUtc: at(8), endUtc: at(8, 30) };
    expect(programMeta(prog)).toBe("S03 E09 · Fishing Cubans · TV-PG · 30 min");
    expect(programHeadline(prog)).toBe("George Lopez");
  });

  it("adds the IMDb score and genre only in the full form, and skips what it does not know", () => {
    const prog = { title: "Ray", year: 2004, imdbRating: 7.7, genre: "Drama", startUtc: at(10), endUtc: at(12) };
    expect(programMeta(prog)).toBe("2004 · 2h");
    expect(programMeta(prog, { full: true })).toBe("2004 · 2h · IMDb 7.7 · Drama");
    expect(programMeta({ title: "Untitled short", startUtc: at(10), endUtc: at(10, 5) })).toBe("5 min");
    expect(programMeta(null)).toBe("");
  });

  it("counts the minutes left in a slot", () => {
    const prog = { startUtc: at(20), endUtc: at(21) };
    expect(minutesLeft(prog, Date.parse(at(20, 20)))).toBe(40);
    expect(minutesLeft(prog, Date.parse(at(22)))).toBe(0);
  });
});

describe("Tv/guideModel — the preview and the hand-off", () => {
  it("previews on desktop and tablet only", () => {
    expect(previewEnabledFor(1280)).toBe(true);
    expect(previewEnabledFor(768)).toBe(true);
    expect(previewEnabledFor(390)).toBe(false);
    expect(previewEnabledFor(NaN)).toBe(false);
  });

  it("asks for the ladder's bottom rung as a plain SDR stereo H.264 encode", () => {
    expect(PREVIEW_BPS).toBe(1_500_000);
    const caps = previewCapabilities();
    expect(caps.supportsHevc).toBe(false);
    expect(caps.supportsHdr).toBe(false);
    expect(caps.supportsMkv).toBe(false);
    expect(caps.maxAudioChannels).toBe(2);
    expect(caps.supportsFmp4).toBe(true); // the browser's own container fact survives
  });

  it("carries Start over to the room as ?restart=1 and reads it back", () => {
    expect(restartHref(7)).toBe("/tv/7?restart=1");
    expect(restartIntent("?restart=1")).toBe(true);
    expect(restartIntent("?q=x")).toBe(false);
    expect(restartIntent("")).toBe(false);
    expect(restartIntent(undefined)).toBe(false);
  });
});
