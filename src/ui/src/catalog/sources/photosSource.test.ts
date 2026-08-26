import { createPhotosSource, photoAspect, toPhotoCard } from "./photosSource";

const row = (id: number, extra: Record<string, unknown> = {}) => ({ id, path: `2019/trip/IMG_${id}.jpg`, kind: "Photo", width: 4000, height: 3000, takenAt: "2019-07-04T12:00:00", gridUrl: `/m/tok/grid/${id}.webp`, ...extra });

function mockFetch(handler: (url: string) => unknown) {
  const calls: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    calls.push(url);
    const body = handler(url);
    return { ok: body != null, status: body != null ? 200 : 404, json: async () => body };
  }));
  return calls;
}
afterEach(() => vi.unstubAllGlobals());

describe("catalog/photosSource — the timeline under an offset, groups by date/album/folder", () => {
  it("maps a photo with its true aspect, a video with a play badge, and a card without a thumbnail to a tinted tile", () => {
    const c = toPhotoCard(row(1));
    expect(c).toMatchObject({ kind: "photo", key: "photo:1", year: 2019, imageUrl: "/m/tok/grid/1.webp" });
    expect(c.aspect).toBeCloseTo(4 / 3, 5);
    expect(c.label).toBeTruthy();
    const v = toPhotoCard(row(2, { kind: "Video", durationSec: 95, videoSynced: false, width: 1080, height: 1920 }));
    expect(v.badges?.[0].label).toBe("▶ 1:35");
    expect(v.aspect).toBeCloseTo(1080 / 1920, 5);
    const bare = toPhotoCard({ id: 3, path: "x/y/IMG_3.HEIC", gridUrl: null, hidden: true });
    expect(bare.title).toBe("IMG_3.HEIC");
    expect(bare.imageUrl.startsWith("data:image/svg+xml")).toBe(true);
    expect(bare.badges?.map((b) => b.label)).toEqual(["hidden"]);
    expect(photoAspect({ id: 1, width: 100, height: 2 })).toBe(2.6);
    expect(photoAspect({ id: 1 })).toBe(1);
  });

  it("pages the flat browse with the count carried, groups under the same hidden flag, walks folders, and opens album/folder headers", async () => {
    const calls = mockFetch((url) => {
      if (url.includes("singleGroupKey=2019%2Ftrip") || url.includes("singleGroupKey=2019")) return { groups: [{ key: "2019", label: "2019", totalItems: 40, items: [row(9)] }] };
      if (url.includes("groupBy=folder")) return { totalGroups: 2, groups: [{ key: "2018", label: "2018", totalItems: 5, items: [row(4)] }, { key: "2019", label: "2019", totalItems: 40, items: [row(5)] }] };
      if (url.includes("/API/Photos/BrowseGroups")) return { totalGroups: 3, groups: [{ key: "2019-07", label: "July 2019", totalItems: 12, items: [row(1), row(2)] }] };
      return url.includes("skip=0") ? { items: [row(1), row(2)], total: 321 } : { items: [row(3)], total: -1 };
    });
    const onOpen = vi.fn();
    const onOpenAlbum = vi.fn();
    const onOpenFolder = vi.fn();
    const s = createPhotosSource({ includeHidden: true, listKey: "k", onOpen, onOpenAlbum, onOpenFolder });
    const first = await s.fetchFlatBand(0, 60, "newest");
    expect(calls[0]).toBe("/API/Photos/Browse?skip=0&top=60&includeHidden=true");
    expect(first.total).toBe(321);
    expect((await s.fetchFlatBand(60, 60, "newest")).total).toBe(321);
    const months = await s.fetchGroupBand!(0, 20, 36, "month", "newest");
    expect(calls[2]).toBe("/API/Photos/BrowseGroups?groupBy=month&groupsSkip=0&groupsTop=20&perGroupTop=36&includeHidden=true");
    expect(months.totalGroups).toBe(3);
    expect(months.groups[0].items.map((i) => i.groupKey)).toEqual(["2019-07", "2019-07"]);
    const more = await s.fetchGroupMore!("2019", 36, 36, "year", "newest");
    expect(more.total).toBe(40);
    const roots = await s.directory!.roots();
    expect(roots.map((r) => [r.id, r.count, r.imageUrl])).toEqual([["2018", 5, "/m/tok/grid/4.webp"], ["2019", 40, "/m/tok/grid/5.webp"]]);
    s.onOpen(months.groups[0].items[0]);
    expect(onOpen).toHaveBeenCalledWith(1);
    s.onOpenGroup!({ key: "summer-2019", label: "Summer", totalItems: 1, renderTotal: 1, items: [] }, "album");
    expect(onOpenAlbum).toHaveBeenCalledWith("summer-2019");
    s.onOpenGroup!({ key: "2019", label: "2019", totalItems: 1, renderTotal: 1, items: [] }, "folder");
    expect(onOpenFolder).toHaveBeenCalledWith("2019");
    s.onOpenGroup!(months.groups[0], "month");
    expect(onOpenAlbum).toHaveBeenCalledTimes(1);
    expect(s.letters).toBeUndefined();
    expect(createPhotosSource({ includeHidden: false, listKey: "k", onOpen }).queryKey).toBe("photos:k");
  });
});
