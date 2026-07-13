import { Input, List, Button, Select } from "antd";
import { useHistory, useLocation } from "react-router-dom";
import LoginForm from "./LoginForm";
import UserPanelHeader from "./UserPanelHeader";
import poweredByBggImage from "../../powered_by_BGG_SM.png";

const { Search } = Input;

const inputLabelStyle = {
  display: "block",
  fontSize: "10px",
  fontWeight: "600",
  color: "var(--sidebar-text-muted)",
  textTransform: "uppercase",
  letterSpacing: "0.8px",
  marginBottom: "5px",
  marginTop: "14px",
};

const searchLetterStyle = {
  fontWeight: "bold",
  position: "absolute",
  width: "100%",
  height: "1em",
  lineHeight: "1em",
  top: "50%",
  left: "0px",
  marginTop: "-0.5em",
};

const searchLetters = ["#","A","B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","T","U","V","W","X","Y","Z"];

// Log Out is not here — it lives in the shared navbar footer, below the theme toggle.
function BoardGameUserPanel({ userData, setSettingsModalOpen, setAdminModalOpen }) {
  return (
    <div className="user-panel">
      <UserPanelHeader userData={userData} setSettingsModalOpen={setSettingsModalOpen} setAdminModalOpen={setAdminModalOpen} />
    </div>
  );
}

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

const sortOptions = [
  { value: "", label: "Alphabetical A → Z" },
  { value: "play_time_asc", label: "Play Time: Short → Long" },
  { value: "play_time_desc", label: "Play Time: Long → Short" },
  { value: "rating_asc", label: "Rating: Low → High" },
  { value: "rating_desc", label: "Rating: High → Low" },
  { value: "complexity_asc", label: "Complexity: Low → High" },
  { value: "complexity_desc", label: "Complexity: High → Low" },
];

function BoardGameNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen, search }) {
  const history = useHistory();
  const location = useLocation();
  const getSelectPopupContainer = (trigger) => trigger.parentElement;

  function navigate(mode, value = "") {
    const params = new URLSearchParams(location.search);
    if (mode) { params.set("mode", mode); } else { params.delete("mode"); }
    if (value && value.trim()) { params.set("value", value.trim()); } else { params.delete("value"); }
    history.push({ pathname: "/boardgames", search: params.toString() ? `?${params.toString()}` : "" });
  }

  function updateParam(key, value) {
    const params = new URLSearchParams(location.search);
    if (value != null && value !== "") { params.set(key, value); } else { params.delete(key); }
    history.push({ pathname: "/boardgames", search: params.toString() ? `?${params.toString()}` : "" });
  }

  function toggleLetter(letter) {
    if (search.startsWith === letter) {
      navigate();
    } else {
      navigate("letter", letter);
    }
  }

  const urlParams = new URLSearchParams(location.search);
  const activePlayers = urlParams.get("players") || undefined;
  const activeAge = urlParams.get("age") || undefined;
  const activeTime = urlParams.get("time") || undefined;
  const activeSort = urlParams.get("sort") || undefined;

  return (
    <>
      {userData ? (
        <BoardGameUserPanel userData={userData} setSettingsModalOpen={setSettingsModalOpen} setAdminModalOpen={setAdminModalOpen} />
      ) : (
        <LoginForm onUserLoggedIn={onUserLoggedIn} popupClassName="boardgame-login-dropdown" />
      )}

      <div id="SearchToolContainer" style={{ padding: "16px 16px 8px", color: "white", borderTop: "1px solid var(--sidebar-border)" }}>
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
          popupClassName="boardgame-login-dropdown"
          getPopupContainer={getSelectPopupContainer}
        />

        <span style={inputLabelStyle}>Age</span>
        <Select
          style={{ width: "100%" }}
          value={activeAge ?? ""}
          onChange={(v) => updateParam("age", v)}
          options={ageOptions}
          popupClassName="boardgame-login-dropdown"
          getPopupContainer={getSelectPopupContainer}
        />

        <span style={inputLabelStyle}>Play Time</span>
        <Select
          style={{ width: "100%" }}
          value={activeTime ?? ""}
          onChange={(v) => updateParam("time", v)}
          options={timeOptions}
          popupClassName="boardgame-login-dropdown"
          getPopupContainer={getSelectPopupContainer}
        />

        <span style={inputLabelStyle}>Sort By</span>
        <Select
          style={{ width: "100%" }}
          value={activeSort ?? ""}
          onChange={(v) => updateParam("sort", v)}
          options={sortOptions}
          popupClassName="boardgame-login-dropdown"
          getPopupContainer={getSelectPopupContainer}
        />

        <span style={inputLabelStyle}>First Letter</span>
        <List
          style={{ paddingBottom: "20px" }}
          grid={{ gutter: [6, 8], xs: 3, sm: 3, md: 3, lg: 3, xl: 4, xxl: 4 }}
          dataSource={searchLetters}
          renderItem={(item) => (
            <List.Item style={{ display: "flex", justifyContent: "center", marginBottom: 0 }}>
              <Button
                // search-letter-btn carries the 36px square + position:relative that
                // searchLetterStyle's absolutely-positioned span needs to center itself.
                className="search-letter-btn"
                onClick={() => toggleLetter(item)}
                style={{
                  width: "36px",
                  backgroundColor: item === search.startsWith ? "var(--accent)" : "var(--sidebar-pill-bg)",
                  color: item === search.startsWith ? "#fff" : "var(--sidebar-text-muted)",
                  borderColor: item === search.startsWith ? "var(--accent)" : "var(--sidebar-input-border)",
                }}
              >
                <span style={searchLetterStyle}>{item}</span>
              </Button>
            </List.Item>
          )}
        />
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
