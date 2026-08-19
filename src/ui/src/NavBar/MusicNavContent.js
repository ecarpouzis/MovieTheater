import { Input } from "antd";
import { useLocation } from "react-router-dom";
import { inputLabelStyle, NavUserBlock, useSectionParams } from "./navShared";

const { Search } = Input;

// Music rail (music-plan.md §2.6): search + the shelf picker + the artists/albums view toggle.
// Filters live in the URL (?view=, ?q=, ?kind=) — the arcade convention — so back/forward and
// reloads restore the same view.

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
  const activeView = params.get("view") === "albums" ? "albums" : "artists";
  const activeQ = params.get("q") || "";
  const activeKind = SHELVES.some((s) => s.key && s.key === params.get("kind")) ? params.get("kind") : "";

  // A view switch changes what a card even is, and a shelf switch changes whether the drilled-into
  // artist (or its open album sheet) is on this shelf at all — both leave them behind.
  const updateParam = (key, value) =>
    setParam(key, value, key === "view" || key === "kind" ? ["artist", "album"] : []);

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

        <span style={inputLabelStyle}>Browse</span>
        {/* Artists on top — it's the default view, and the rail should read in the same order. */}
        <div style={{ display: "flex", flexDirection: "column", gap: "6px" }}>
          <button style={pillStyle(activeView === "artists")} onClick={() => updateParam("view", null)}>
            Artists
          </button>
          <button style={pillStyle(activeView === "albums")} onClick={() => updateParam("view", "albums")}>
            Albums
          </button>
        </div>
      </div>
    </>
  );
}

export default MusicNavContent;
