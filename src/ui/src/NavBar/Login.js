import { useState, useEffect } from "react";
import { Button, Input, Tooltip, AutoComplete } from "antd";
import { InfoCircleOutlined, UserOutlined } from "@ant-design/icons";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../MovieAPI";
import "./Login.css";

//Function component Login
//Props:
//  userData - Stores user data, used to determine if the Login component displays ways to log in, or user information and Logout
//  onUserLoggedIn - Hook to handle passing user login event to App.js
function Login({ userData, setUserData, onUserLoggedIn }) {
  const history = useHistory();
  //Hook to store a list of all users
  const [userlist, setUserlist] = useState([]);
  const [filteredUserlist, setFilteredUserlist] = useState([]);
  const [searchValue, setSearchValue] = useState(null);

  //When a name in the Username dropdown is selected, log that user in
  const onSelect = (value) => {
    onUserLoggedIn(value);
  };

  //When the LoginButton is clicked, log in as the user in the input field
  const onUserClickedLoginButton = () => {
    const user = userlist.find((obj) => obj.value === searchValue);
    if (user) {
      onUserLoggedIn(user.value);
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
      <AutoComplete
        options={filteredUserlist}
        className="login-autocomplete"
        onSelect={onSelect}
        onSearch={handleSearch}
      >
        <div>
          <Input
            placeholder="Username"
            prefix={<UserOutlined className="site-form-item-icon" />}
            className="login-input"
            onChange={(e) => setSearchValue(e.target.value)}
            value={searchValue}
            suffix={
              <Tooltip title="This website purposely requires no password to log in.">
                <InfoCircleOutlined className="login-tooltip-icon" />
              </Tooltip>
            }
          />
          <Button
            type="primary"
            className="login-button"
            onClick={onUserClickedLoginButton}
          >
            {">"}
          </Button>
        </div>
      </AutoComplete>
    </div>
  );

  //When a user is logged in, render information about that user and a button to log out
  function getLoggedInDisplay(userData) {
    return (
      <div className="user-panel">
        <div className="user-panel-header">
          <div className="user-avatar">
            👤
          </div>
          <span className="user-username">{userData.username}</span>
        </div>
        <div className="stat-row" onClick={() => navigateToBrowseSearch("seen")}>
          <span className="stat-icon stat-icon--seen fas fa-film"></span>
          <span className="stat-label">Seen</span>
          <span className="stat-count">{userData.moviesSeen.length}</span>
        </div>
        <div className="stat-row" onClick={() => navigateToBrowseSearch("want")}>
          <span className="stat-icon stat-icon--want fas fa-heart"></span>
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
