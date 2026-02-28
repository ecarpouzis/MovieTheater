import { useState, useEffect } from "react";
import { Button, Input, Tooltip, AutoComplete } from "antd";
import { InfoCircleOutlined, UserOutlined } from "@ant-design/icons";
import { useHistory } from "react-router-dom";
import { MovieAPI } from "../MovieAPI";

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
    var filteredList = userlist.filter((e) => {
      return e.value.toLowerCase().includes(value.toLowerCase());
    });
    setFilteredUserlist(filteredList);
  };

  //? - Why is the array at the end of this empty, since this isn't happening based on some value, is useEffect appropriate?
  //Get and store a list of website users, which will be used as the default values of the autocomplete box.
  //This only gets run once, when the component is rendered (intended in this scenario)
  useEffect(
    () =>
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
        }),
    []
  );

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

  const statRowStyle = {
    display: "flex",
    alignItems: "center",
    gap: "10px",
    padding: "7px 0",
    cursor: "pointer",
    borderRadius: "4px",
  };

  function logoutUser() {
    setUserData();
    window.localStorage.clear();
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
    <div id="LoginContainer" style={{ color: "white" }}>
      <span style={{ fontWeight: "bold", fontSize: "18px" }}>LOG IN</span>
      <br />
      <br />
      <AutoComplete
        options={filteredUserlist}
        style={{
          width: 180,
        }}
        onSelect={onSelect}
        onSearch={handleSearch}
      >
        <div>
          <Input
            placeholder="Username"
            prefix={<UserOutlined className="site-form-item-icon" />}
            style={{
              width: "135px",
              borderTopRightRadius: "0px",
              borderBottomRightRadius: "0px",
            }}
            onChange={(e) => setSearchValue(e.target.value)}
            value={searchValue}
            suffix={
              <Tooltip title="This website purposely requires no password to log in.">
                <InfoCircleOutlined style={{ color: "rgba(0,0,0,.45)" }} />
              </Tooltip>
            }
          />
          <Button
            type="primary"
            style={{
              borderTopLeftRadius: "0px",
              borderBottomLeftRadius: "0px",
            }}
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
      <div style={{ padding: "16px 16px 8px", color: "white" }}>
        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: "10px",
            marginBottom: "14px",
            paddingBottom: "12px",
            borderBottom: "1px solid #1e3a57",
          }}
        >
          <div
            style={{
              width: "34px",
              height: "34px",
              borderRadius: "50%",
              background: "#1e3a57",
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              fontSize: "16px",
              flexShrink: 0,
            }}
          >
            👤
          </div>
          <span style={{ fontWeight: "600", fontSize: "14px", color: "rgba(255,255,255,0.9)" }}>{userData.username}</span>
        </div>
        <div style={statRowStyle} onClick={() => navigateToBrowseSearch("seen")}>
          <span className="fas fa-film" style={{ color: "#4169e3", fontSize: "15px", width: "18px", textAlign: "center" }}></span>
          <span style={{ color: "#c8d8e8", fontSize: "13px", flex: 1 }}>Seen</span>
          <span style={{ background: "#1e3a57", color: "#7eb3e0", borderRadius: "10px", padding: "1px 9px", fontSize: "12px", fontWeight: "bold" }}>
            {userData.moviesSeen.length}
          </span>
        </div>
        <div style={statRowStyle} onClick={() => navigateToBrowseSearch("want")}>
          <span className="fas fa-heart" style={{ color: "#dc143c", fontSize: "15px", width: "18px", textAlign: "center" }}></span>
          <span style={{ color: "#c8d8e8", fontSize: "13px", flex: 1 }}>Want to Watch</span>
          <span style={{ background: "#1e3a57", color: "#7eb3e0", borderRadius: "10px", padding: "1px 9px", fontSize: "12px", fontWeight: "bold" }}>
            {userData.moviesToWatch.length}
          </span>
        </div>
        <button
          onClick={logoutUser}
          style={{
            marginTop: "10px",
            width: "100%",
            background: "transparent",
            border: "1px solid #2a4a6e",
            color: "#7eb3e0",
            borderRadius: "4px",
            cursor: "pointer",
            padding: "5px 0",
            fontSize: "12px",
            fontWeight: "600",
            letterSpacing: "0.5px",
          }}
        >
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
