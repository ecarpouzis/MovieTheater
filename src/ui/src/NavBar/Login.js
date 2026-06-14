import { useState, useEffect } from "react";
import { Button, Input, Tooltip, AutoComplete } from "antd";
import { InfoCircleOutlined, UserOutlined, LockOutlined } from "@ant-design/icons";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../MovieAPI";
import "./Login.css";

//Function component Login
//Props:
//  userData - Stores user data, used to determine if the Login component displays ways to log in, or user information and Logout
//  onUserLoggedIn - Hook to handle passing user login event to App.js
//  setSettingsModalOpen - setter for settings modal state
function Login({ userData, setUserData, onUserLoggedIn, setSettingsModalOpen }) {
  const history = useHistory();
  //Hook to store a list of all users
  const [userlist, setUserlist] = useState([]);
  const [filteredUserlist, setFilteredUserlist] = useState([]);
  const [searchValue, setSearchValue] = useState(null);
  //Password-protected accounts: revealed only after the server asks for one
  const [requiresPassword, setRequiresPassword] = useState(false);
  const [password, setPassword] = useState("");
  const [loginMessage, setLoginMessage] = useState(null);

  //Attempt a login; if the account is password-protected the server responds with
  //requiresPassword and we reveal the password field instead of logging in.
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

  //When a name in the Username dropdown is selected, log that user in
  const onSelect = (value) => {
    setSearchValue(value);
    setRequiresPassword(false);
    setPassword("");
    setLoginMessage(null);
    attemptLogin(value);
  };

  //When the LoginButton is clicked, log in as the user in the input field
  const onUserClickedLoginButton = () => {
    const user = userlist.find((obj) => obj.value === searchValue);
    if (user) {
      attemptLogin(user.value, requiresPassword ? password : undefined);
    }
  };

  //When text is entered into the Login box, return a list of users that include the entered text for Autocomplete
  const handleSearch = (value) => {
    const filteredList = userlist.filter((e) => {
      return e.value.toLowerCase().includes(value.toLowerCase());
    });
    setFilteredUserlist(filteredList);
  };

  //? - Why is the array at the end of this empty, since this isn't happening based on some value, is useEffect appropriate?
  //Get and store a list of website users, which will be used as the default values of the autocomplete box.
  //This only gets run once, when the component is rendered (intended in this scenario)
  useEffect(() => {
    MovieAPI.getUsers()
      .then((response) => {
        return response.json();
      })
      .then((responseData) => {
        const responseDataMap = responseData.map((x) => ({
          value: x,
        }));

        setUserlist(responseDataMap);
        setFilteredUserlist(responseDataMap);
      });
  }, []);

  function logoutUser() {
    fetch("/API/Logout", { method: "POST" }).finally(() => {
      setUserData();
      window.localStorage.clear();
    });
  }

  function navigateToBrowseSearch(mode) {
    const params = new URLSearchParams();
    params.set("mode", mode);

    history.push({
      pathname: "/",
      search: `?${params.toString()}`,
    });
  }

  //When a user isn't logged in, render a login tool which enables the user to log in
  const getLoginTools = () => (
    <div id="LoginContainer" className="login-container">
      <span className="login-title">LOG IN</span>
      <br />
      <br />
      <div className="login-input-row">
        <AutoComplete
          options={filteredUserlist}
          className="login-autocomplete"
          popupClassName="login-user-dropdown"
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
            onPressEnter={onUserClickedLoginButton}
            suffix={
              <Tooltip title="Most accounts need no password. If an account has one set, you'll be asked for it.">
                <InfoCircleOutlined className="login-tooltip-icon" />
              </Tooltip>
            }
          />
        </AutoComplete>
        <Button type="primary" className="login-button" onClick={onUserClickedLoginButton}>
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
          onPressEnter={() => attemptLogin(searchValue, password)}
          autoFocus
        />
      )}
      {loginMessage && <div className="login-message">{loginMessage}</div>}
    </div>
  );

  //When a user is logged in, render information about that user and a button to log out
  function getLoggedInDisplay(userData) {
    return (
      <div className="user-panel">
        <div className="user-panel-header">
          <div className="user-avatar"><UserOutlined /></div>
          <span className="user-username">{userData.username}</span>
          <button className="settings-icon-btn" onClick={() => setSettingsModalOpen(true)} title="User Settings">
            ⚙️
          </button>
        </div>
        <div className="stat-row" onClick={() => navigateToBrowseSearch("seen")}>
          <span className="stat-icon stat-icon--seen">🎬</span>
          <span className="stat-label">Seen</span>
          <span className="stat-count">{userData.moviesSeen.length}</span>
        </div>
        <div className="stat-row" onClick={() => navigateToBrowseSearch("want")}>
          <span className="stat-icon stat-icon--want">♥</span>
          <span className="stat-label">Want to Watch</span>
          <span className="stat-count">{userData.moviesToWatch.length}</span>
        </div>
        <button className="logout-button" onClick={logoutUser}>
          Log Out
        </button>
      </div>
    );
  }

  //Render LoggedInDisplay or LoginTools based on whether userData is populated
  if (userData) {
    return getLoggedInDisplay(userData);
  } else {
    return getLoginTools();
  }
}
export default Login;
