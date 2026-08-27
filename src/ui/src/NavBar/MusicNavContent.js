import { Input } from "antd";
import { useLocation } from "react-router-dom";
import { inputLabelStyle, NavUserBlock, useSectionParams } from "./navShared";
import useIsMobile from "../hooks/useIsMobile";

const { Search } = Input;

// Music rail (music-plan.md §2.6): search + the shelf picker + the artists/albums view toggle.
// Filters live in the URL (?tab=, ?q=, ?kind=) — the arcade convention — so back/forward and
// reloads restore the same list. (`?view=` is the catalog switcher's — Grid/Wall/Shelves… — site-wide;
// the artists/albums toggle used to own that name and MusicPage still honours a legacy link.)

// The shelves, mirroring MUSIC_KINDS in MusicPage. Music is the empty key because it is the SERVER's
// default: browsing must work without anything having been classified, so "no ?kind=" is the
// library and the two named shelves are the opt-in.
const SHELVES = [
  { key: "", label: "Music" },
  { key: "comedy", label: "Comedy" },
  { key: "audiobook", label: "Audiobooks" },
];

function MusicNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen }) {
  const location = useLocation();
  const setParam = useSectionParams("/music");

  const params = new URLSearchParams(location.search);
  const isMobile = useIsMobile();
  const activeQ = params.get("q") || "";
  const activeKind = SHELVES.some((s) => s.key && s.key === params.get("kind")) ? params.get("kind") : "";

  // A tab switch changes what a card even is, and a shelf switch changes whether the drilled-into
  // artist (or its open album sheet) is on this shelf at all — both leave them behind.
  const updateParam = (key, value) =>
    setParam(key, value, key === "tab" || key === "kind" ? ["artist", "album"] : []);

  // One pill, two callers: the view toggle and the shelf picker are the same control in the same
  // rail, so they share the shape and only differ in which value they compare against.
  const pillStyle = (on) => ({
    flex: 1,
    padding: "6px 0",
    borderRadius: "6px",
    border: "1px solid var(--sidebar-input-border)",
    cursor: "pointer",
    fontSize: "12px",
    background: on ? "var(--accent)" : "var(--sidebar-pill-bg)",
    color: on ? "#fff" : "var(--sidebar-text-muted)",
  });

  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen} setAdminModalOpen={setAdminModalOpen} />

      <div className="nav-search-tools" style={{ padding: "16px 16px 8px", borderTop: "1px solid var(--sidebar-border)" }}>
        {/* On desktop the search is the SectionBar's centre box (R9 S1d); the rail keeps it for the
            phone drawer, where the bar has no search slot. */}
        {isMobile && (
          <>
            <span style={{ ...inputLabelStyle, marginTop: 0 }}>Search</span>
            <form onSubmit={(e) => e.preventDefault()}>
              <Search
                placeholder="Artist, album, song"
                style={{ width: "100%" }}
                enterKeyHint="search"
                defaultValue={activeQ}
                allowClear
                onSearch={(v) => updateParam("q", v && v.trim())}
                enterButton
              />
            </form>
          </>
        )}

        {/* The shelf comes FIRST because it decides what "Artists" even lists. Comedy and audiobooks
            are hidden from the music library by default (MusicArtist.Kind) — this is where they are,
            and putting them in the rail is what makes the exclusion a choice rather than a
            disappearance. */}
        <span style={inputLabelStyle}>Shelf</span>
        <div style={{ display: "flex", flexDirection: "column", gap: "6px" }}>
          {SHELVES.map((s) => (
            <button
              key={s.key || "music"}
              style={pillStyle(activeKind === s.key)}
              onClick={() => updateParam("kind", s.key || null)}
            >
              {s.label}
            </button>
          ))}
        </div>

        {/* The Artists / Albums pair is gone (R9 S1b): Music is ONE section — "one per artist" is the
            catalog's Items pill in the SectionBar, "by artist" its Group pill. */}
      </div>
    </>
  );
}

export default MusicNavContent;
