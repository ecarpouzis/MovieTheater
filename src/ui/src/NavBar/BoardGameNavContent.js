import { useState, useEffect } from "react";
import { Input, List, Button, AutoComplete, Tooltip } from "antd";
import { InfoCircleOutlined, UserOutlined } from "@ant-design/icons";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../MovieAPI";

const { Search } = Input;

const sectionHeaderStyle = {
  display: "block",
  fontSize: "10px",
  fontWeight: "700",
  color: "#6b8aad",
  textTransform: "uppercase",
  letterSpacing: "1.5px",
  marginBottom: "12px",
  paddingBottom: "8px",
  borderBottom: "1px solid #1e3a57",
};

const inputLabelStyle = {
  display: "block",
  fontSize: "10px",
  fontWeight: "600",
  color: "#8fa8c0",
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
        <div className="user-avatar">👤</div>
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
        popupClassName="login-user-dropdown"
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

function BoardGameNavContent({ userData, setUserData, onUserLoggedIn, setSettingsModalOpen, search }) {
  const history = useHistory();

  function navigate(mode, value = "") {
    const params = new URLSearchParams();
    if (mode) params.set("mode", mode);
    if (value && value.trim()) params.set("value", value.trim());
    history.push({ pathname: "/boardgames", search: params.toString() ? `?${params.toString()}` : "" });
  }

  function toggleLetter(letter) {
    if (search.startsWith === letter) {
      navigate();
    } else {
      navigate("letter", letter);
    }
  }

  return (
    <>
      {userData ? (
        <BoardGameUserPanel userData={userData} setUserData={setUserData} setSettingsModalOpen={setSettingsModalOpen} />
      ) : (
        <BoardGameLoginForm onUserLoggedIn={onUserLoggedIn} />
      )}

      <div id="SearchToolContainer" style={{ padding: "16px 16px 8px", color: "white", borderTop: "1px solid #1e3a57" }}>
        <span style={sectionHeaderStyle}>Search</span>

        <span style={{ ...inputLabelStyle, marginTop: 0 }}>Game Title</span>
        <Search
          placeholder="Title"
          style={{ width: "100%" }}
          onSearch={(v) => (v && v.trim() ? navigate("title", v) : navigate())}
          enterButton
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
                  backgroundColor: item === search.startsWith ? "#1890ff" : "rgba(255,255,255,0.08)",
                  color: item === search.startsWith ? "white" : "rgba(255,255,255,0.75)",
                  borderColor: item === search.startsWith ? "#1890ff" : "rgba(255,255,255,0.15)",
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
