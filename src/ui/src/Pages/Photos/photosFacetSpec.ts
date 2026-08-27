/**
 * The Photos browse's facets (R9 S2c) over the reel — the `/photos/browse` catalog of the Timeline
 * shelf (the custom Timeline page stays the section root; the Gallery is its own subsection and
 * never mixes in). The same `q/f/x/y` URL contract as every other section; the state maps onto
 * `/API/Photos/Browse*`' own `PhotoBrowseFilterQuery` params, and the option lists come from
 * `/API/Photos/Facets` (counted over the reachable scope, per hidden toggle).
 *
 *   f=album:summer-2019    → album=summer-2019     (x=album: → exAlbum)
 *   f=person:4             → person=4              (x=person: → exPerson)  affirmed tags only
 *   f=kind:video           → kind=video            one value
 *   f=camera:iPhone 12     → camera=iPhone 12      (x=camera: → exCamera)
 *   y=2015-2019            → yearMin=2015&yearMax=2019
 *   q=beach                → q=beach               path / location text
 */
import type { FacetOptionRow, FacetSpec, FacetState, FacetValue } from "../../catalog/rail/facetSpec";

export const PHOTOS_ENTITY_PARAMS = ["photo"] as const;

interface FacetCountsDto {
  total?: number;
  decades?: { value: string; label: string; count: number }[];
  years?: { value: string; label: string; count: number }[];
  albums?: { value: string; label: string; count: number }[];
  people?: { value: number; label: string; count: number }[];
  kinds?: { value: string; label: string; count: number }[];
  cameras?: { value: string; label: string; count: number }[];
}

const KIND_LABELS: Record<string, string> = { photo: "Photos", video: "Videos" };

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const r = await fetch(url, { signal });
  if (!r.ok) throw new Error(`${url} → ${r.status}`);
  return (await r.json()) as T;
}

/**
 * The spec for one viewer over one hidden toggle: `identity` carries the toggle and the page's
 * structural refresh key (a curation refresh re-counts), so the loaded lists follow the reel.
 */
export function photosFacetSpec(identity: string, includeHidden: boolean): FacetSpec {
  return {
    identity: `photos:${identity}:${includeHidden ? "h" : "v"}`,
    noun: "photos",
    text: true,
    years: { decadesKey: "decades", decadePills: false },
    facets: [
      { key: "album", token: "album", label: "Album", one: "Album", valueType: "string", defaultOpen: true },
      { key: "person", token: "person", label: "People", one: "Person", valueType: "number", defaultOpen: true },
      { key: "kind", token: "kind", label: "Kind", one: "Kind", valueType: "string", render: "pill", defaultOpen: true, excludable: false, labelOf: (v) => KIND_LABELS[String(v)] ?? String(v) },
      { key: "camera", token: "camera", label: "Camera", one: "Camera", valueType: "string" },
    ],
    async loadFacets(signal) {
      const f = await getJson<FacetCountsDto>(`/API/Photos/Facets${includeHidden ? "?includeHidden=true" : ""}`, signal);
      const rows = (list: { value: string | number; label: string; count: number }[] | undefined): FacetOptionRow[] =>
        (list ?? []).map((r) => ({ value: r.value, label: r.label, count: r.count }));
      return { decades: rows(f.decades), album: rows(f.albums), person: rows(f.people), kind: rows(f.kinds), camera: rows(f.cameras) };
    },
  };
}

/** A parse-only spec: the URL codec needs the tokens, not the counts. */
export const PHOTOS_PARSE_SPEC: FacetSpec = photosFacetSpec("parse", false);

const lastOf = (list: FacetValue[] | undefined): string => (list?.length ? String(list[list.length - 1]) : "");

/** The state in `/API/Photos/Browse*`' own vocabulary (`PhotoBrowseFilterQuery`), as a query string fragment ("" when empty). */
export function photosFilterParams(state: FacetState): string {
  const p = new URLSearchParams();
  const put = (key: string, values: FacetValue[] | undefined) => { for (const v of values ?? []) p.append(key, String(v)); };
  if (state.q.trim()) p.set("q", state.q.trim());
  put("album", state.include.album); put("exAlbum", state.exclude.album);
  put("person", state.include.person); put("exPerson", state.exclude.person);
  put("camera", state.include.camera); put("exCamera", state.exclude.camera);
  const kind = lastOf(state.include.kind).toLowerCase();
  if (kind === "photo" || kind === "video") p.set("kind", kind);
  if (state.yearMin != null) p.set("yearMin", String(state.yearMin));
  if (state.yearMax != null) p.set("yearMax", String(state.yearMax));
  return p.toString();
}
