import { useState, useEffect } from "react";
import { Button, Input, Tooltip, AutoComplete } from "antd";
import { InfoCircleOutlined, UserOutlined, LockOutlined } from "@ant-design/icons";
import { MovieAPI } from "../MovieAPI";

// The shared passwordless/password login form, used by every feature's nav (Movies, Board Games,
// Arcade). It owns the two-step flow: pick a username → if the server says the account is
// password-protected, reveal a password field and retry. Extracted from Login.js so the arcade and
// boardgame navs no longer duplicate a bespoke form that ignored the requiresPassword result and
// silently failed for password accounts.
//
// Props:
//   onUserLoggedIn(username, password?) → Promise<{ ok, requiresPassword?, message? }> (from App.js)
//   popupClassName — antd dropdown class (defaults to the shared token-styled login dropdown)
function LoginForm({ onUserLoggedIn, popupClassName = "login-user-dropdown" }) {
  const [userlist, setUserlist] = useState([]);
  const [filteredUserlist, setFilteredUserlist] = useState([]);
  const [searchValue, setSearchValue] = useState(null);
  const [requiresPassword, setRequiresPassword] = useState(false);
  const [password, setPassword] = useState("");
  const [loginMessage, setLoginMessage] = useState(null);

  useEffect(() => {
    MovieAPI.getUsers()
      .then((response) => response.json())
      .then((responseData) => {
        const mapped = responseData.map((x) => ({ value: x }));
        setUserlist(mapped);
        setFilteredUserlist(mapped);
      });
  }, []);

  // Attempt a login; if the account is password-protected the server responds with
  // requiresPassword and we reveal the password field instead of logging in.
  const attemptLogin = (username, pass) => {
    if (!username) return;
    onUserLoggedIn(username, pass).then((result) => {
      if (result.ok) {
        setRequiresPassword(false);
        setPassword("");
        setLoginMessage(null);
        return;
      }
      if (result.requiresPassword) {
        setRequiresPassword(true);
        setLoginMessage(result.message ?? "This account is password-protected.");
      } else {
        setLoginMessage(result.message ?? "Login failed.");
      }
    });
  };

  const onSelect = (value) => {
    setSearchValue(value);
    setRequiresPassword(false);
    setPassword("");
    setLoginMessage(null);
    attemptLogin(value);
  };

  const onClickLogin = () => {
    const user = userlist.find((obj) => obj.value === searchValue);
    if (user) attemptLogin(user.value, requiresPassword ? password : undefined);
  };

  const handleSearch = (value) => {
    setFilteredUserlist(userlist.filter((e) => e.value.toLowerCase().includes(value.toLowerCase())));
  };

  return (
    <div id="LoginContainer" className="login-container">
      <span className="login-title">LOG IN</span>
      <br />
      <br />
      <div className="login-input-row">
        <AutoComplete
          options={filteredUserlist}
          className="login-autocomplete"
          classNames={{ popup: { root: popupClassName } }}
          onSelect={onSelect}
          onSearch={handleSearch}
          onChange={(value) => setSearchValue(value)}
          value={searchValue}
          getPopupContainer={(trigger) => trigger.parentElement}
        >
          <Input
            placeholder="Username"
            prefix={<UserOutlined className="site-form-item-icon" />}
            className="login-input"
            // Without it, a tablet keyboard labels Enter "Next" and moves focus to the ▸ button rather
            // than logging in. (No <form> here — the AutoComplete owns the field; the hint is enough to
            // make the action key send Enter, which onPressEnter already handles.)
            enterKeyHint="go"
            onPressEnter={onClickLogin}
            suffix={
              <Tooltip title="Most accounts need no password. If an account has one set, you'll be asked for it.">
                <InfoCircleOutlined className="login-tooltip-icon" />
              </Tooltip>
            }
          />
        </AutoComplete>
        <Button type="primary" className="login-button" onClick={onClickLogin}>
          {">"}
        </Button>
      </div>
      {requiresPassword && (
        <Input.Password
          placeholder="Password"
          className="login-input login-password-input"
          prefix={<LockOutlined className="site-form-item-icon" />}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          enterKeyHint="go"
          onPressEnter={() => attemptLogin(searchValue, password)}
          autoFocus
        />
      )}
      {loginMessage && <div className="login-message">{loginMessage}</div>}
    </div>
  );
}

export default LoginForm;
