import { Input, Select } from "antd";
import { useHistory, useLocation } from "react-router-dom";
import { inputLabelStyle, getPopupContainer, NavUserBlock, useSectionParams } from "./navShared";
import poweredByBggImage from "../../powered_by_BGG_SM.png";

const { Search } = Input;

const playerOptions = [
  { value: "", label: "Any player count" },
  ...[1,2,3,4,5,6,7,8].map((n) => ({
    value: String(n),
    label: n === 8 ? "8+ players" : `${n} player${n === 1 ? "" : "s"}`,
  })),
];

const ageOptions = [
  { value: "", label: "Any age" },
  ...[5,6,7,8,9,10,12,14,16,18].map((a) => ({ value: String(a), label: `Age ${a}+` })),
];

const timeOptions = [
  { value: "", label: "Any length" },
  ...[15,20,25,30,35,40,45,50,55,60,65,70,75,80,85,90,100,110,120,150,180].map((t) => ({
    value: String(t),
    label: `Up to ${t} min`,
  })),
];

function BoardGameNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen, search }) {
  const history = useHistory();
  const location = useLocation();
  const updateParam = useSectionParams("/boardgames");
  function navigate(mode, value = "") {
    const params = new URLSearchParams(location.search);
    if (mode) { params.set("mode", mode); } else { params.delete("mode"); }
    if (value && value.trim()) { params.set("value", value.trim()); } else { params.delete("value"); }
    history.push({ pathname: "/boardgames", search: params.toString() ? `?${params.toString()}` : "" });
  }

  const urlParams = new URLSearchParams(location.search);
  const activePlayers = urlParams.get("players") || undefined;
  const activeAge = urlParams.get("age") || undefined;
  const activeTime = urlParams.get("time") || undefined;

  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen} setAdminModalOpen={setAdminModalOpen} />

      <div className="nav-search-tools" style={{ padding: "16px 16px 8px", color: "white", borderTop: "1px solid var(--sidebar-border)" }}>
        <span style={{ ...inputLabelStyle, marginTop: 0 }}>Game Title</span>
        {/* Single-field <form> so a tablet keyboard's Enter searches instead of jumping focus to the
            Players dropdown below (see SearchTools for the full note). onSearch still navigates. */}
        <form onSubmit={(e) => e.preventDefault()}>
          <Search
            placeholder="Title"
            style={{ width: "100%" }}
            enterKeyHint="search"
            onSearch={(v) => (v && v.trim() ? navigate("title", v) : navigate())}
            enterButton
          />
        </form>

        <span style={{ ...inputLabelStyle, marginTop: "18px" }}>Players</span>
        <Select
          style={{ width: "100%" }}
          value={activePlayers ?? ""}
          onChange={(v) => updateParam("players", v)}
          options={playerOptions}
          classNames={{ popup: { root: "nav-dropdown" } }}
          getPopupContainer={getPopupContainer}
        />

        <span style={inputLabelStyle}>Age</span>
        <Select
          style={{ width: "100%" }}
          value={activeAge ?? ""}
          onChange={(v) => updateParam("age", v)}
          options={ageOptions}
          classNames={{ popup: { root: "nav-dropdown" } }}
          getPopupContainer={getPopupContainer}
        />

        <span style={inputLabelStyle}>Play Time</span>
        <Select
          style={{ width: "100%" }}
          value={activeTime ?? ""}
          onChange={(v) => updateParam("time", v)}
          options={timeOptions}
          classNames={{ popup: { root: "nav-dropdown" } }}
          getPopupContainer={getPopupContainer}
        />

        {/* Sort left the rail in R9 S1: the SectionBar's Sort pill is the one sort control. */}

        {/* The A–Z rail grid is gone: quick-scroll is the on-page CatalogPager now (the Music/
            Arcade convention) — a letter tap scrolls the list instead of re-filtering it.
            ?mode=letter URLs keep working; there's just no rail UI writing them any more. */}
      </div>

      <div style={{ marginTop: "auto", padding: "12px", borderTop: "1px solid var(--sidebar-border)" }}>
        <img
          src={poweredByBggImage}
          alt="Powered by BoardGameGeek"
          style={{ width: "100%", display: "block", borderRadius: "6px" }}
        />
      </div>
    </>
  );
}

export default BoardGameNavContent;
