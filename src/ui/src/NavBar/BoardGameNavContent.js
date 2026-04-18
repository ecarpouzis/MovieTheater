import { useState, useEffect } from "react";
import { Input, List, Button, AutoComplete, Tooltip, Select } from "antd";
import { InfoCircleOutlined, UserOutlined } from "@ant-design/icons";
import { useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../MovieAPI";

const { Search } = Input;

const sectionHeaderStyle = {
  display: "block",
  fontSize: "10px",
  fontWeight: "700",
  color: "#7abf96",
  textTransform: "uppercase",
  letterSpacing: "1.5px",
  marginBottom: "12px",
  paddingBottom: "8px",
  borderBottom: "1px solid #2a6040",
};

const inputLabelStyle = {
  display: "block",
  fontSize: "10px",
  fontWeight: "600",
  color: "#9fcfad",
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

function BoardGameUserPanel({ userData, setUserData, setSettingsModalOpen }) {
  function logout() {
    fetch("/API/Logout", { method: "POST" }).finally(() => {
      setUserData();
      window.localStorage.clear();
    });
  }

  return (
    <div className="user-panel">
      <div className="user-panel-header">
        <div className="user-avatar"><UserOutlined /></div>
        <span className="user-username">{userData.username}</span>
        <button className="settings-icon-btn" onClick={() => setSettingsModalOpen(true)} title="User Settings">
          ⚙️
        </button>
      </div>
      <button className="logout-button" onClick={logout}>
        Log Out
      </button>
    </div>
  );
}

function BoardGameLoginForm({ onUserLoggedIn }) {
  const [userlist, setUserlist] = useState([]);
  const [filtered, setFiltered] = useState([]);
  const [inputValue, setInputValue] = useState(null);

  useEffect(() => {
    MovieAPI.getUsers()
      .then((r) => r.json())
      .then((data) => {
        const mapped = data.map((x) => ({ value: x }));
        setUserlist(mapped);
        setFiltered(mapped);
      });
  }, []);

  function handleLogin() {
    const match = userlist.find((x) => x.value === inputValue);
    if (match) onUserLoggedIn(match.value);
  }

  return (
    <div id="LoginContainer" className="login-container">
      <span className="login-title">LOG IN</span>
      <br />
      <br />
      <AutoComplete
        options={filtered}
        className="login-autocomplete"
        popupClassName="login-user-dropdown boardgame-login-dropdown"
        onSelect={onUserLoggedIn}
        onSearch={(v) => setFiltered(userlist.filter((e) => e.value.toLowerCase().includes(v.toLowerCase())))}
        getPopupContainer={(t) => t.parentElement}
      >
        <div style={{ display: "flex", gap: "0", alignItems: "stretch" }}>
          <Input
            placeholder="Username"
            prefix={<UserOutlined />}
            className="login-input"
            onChange={(e) => setInputValue(e.target.value)}
            value={inputValue}
            suffix={
              <Tooltip title="This website purposely requires no password to log in.">
                <InfoCircleOutlined className="login-tooltip-icon" />
              </Tooltip>
            }
          />
          <Button type="primary" className="login-button" onClick={handleLogin}>
            {">"}
          </Button>
        </div>
      </AutoComplete>
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
  { value: "complexity_asc", label: "Complexity: Low → High" },
  { value: "complexity_desc", label: "Complexity: High → Low" },
];

function BoardGameNavContent({ userData, setUserData, onUserLoggedIn, setSettingsModalOpen, search }) {
  const history = useHistory();
  const location = useLocation();

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
        <BoardGameUserPanel userData={userData} setUserData={setUserData} setSettingsModalOpen={setSettingsModalOpen} />
      ) : (
        <BoardGameLoginForm onUserLoggedIn={onUserLoggedIn} />
      )}

      <div id="SearchToolContainer" style={{ padding: "16px 16px 8px", color: "white", borderTop: "1px solid #2a6040" }}>
        <span style={sectionHeaderStyle}>Search</span>

        <span style={{ ...inputLabelStyle, marginTop: 0 }}>Game Title</span>
        <Search
          placeholder="Title"
          style={{ width: "100%" }}
          onSearch={(v) => (v && v.trim() ? navigate("title", v) : navigate())}
          enterButton
        />

        <span style={{ ...inputLabelStyle, marginTop: "18px" }}>Players</span>
        <Select
          style={{ width: "100%" }}
          value={activePlayers ?? ""}
          onChange={(v) => updateParam("players", v)}
          options={playerOptions}
          popupClassName="boardgame-login-dropdown"
        />

        <span style={inputLabelStyle}>Age</span>
        <Select
          style={{ width: "100%" }}
          value={activeAge ?? ""}
          onChange={(v) => updateParam("age", v)}
          options={ageOptions}
          popupClassName="boardgame-login-dropdown"
        />

        <span style={inputLabelStyle}>Play Time</span>
        <Select
          style={{ width: "100%" }}
          value={activeTime ?? ""}
          onChange={(v) => updateParam("time", v)}
          options={timeOptions}
          popupClassName="boardgame-login-dropdown"
        />

        <span style={inputLabelStyle}>Sort By</span>
        <Select
          style={{ width: "100%" }}
          value={activeSort ?? ""}
          onChange={(v) => updateParam("sort", v)}
          options={sortOptions}
          popupClassName="boardgame-login-dropdown"
        />

        <span style={inputLabelStyle}>First Letter</span>
        <List
          style={{ paddingBottom: "20px" }}
          grid={{ gutter: [6, 8], xs: 3, sm: 3, md: 3, lg: 3, xl: 4, xxl: 4 }}
          dataSource={searchLetters}
          renderItem={(item) => (
            <List.Item style={{ display: "flex", justifyContent: "center", marginBottom: 0 }}>
              <Button
                onClick={() => toggleLetter(item)}
                style={{
                  width: "36px",
                  backgroundColor: item === search.startsWith ? "#2db56d" : "rgba(100,220,160,0.08)",
                  color: item === search.startsWith ? "#fff" : "rgba(180,240,200,0.75)",
                  borderColor: item === search.startsWith ? "#2db56d" : "rgba(100,220,160,0.2)",
                }}
              >
                <span style={searchLetterStyle}>{item}</span>
              </Button>
            </List.Item>
          )}
        />
      </div>
    </>
  );
}

export default BoardGameNavContent;
