import { useState, useEffect } from "react";
import { Button, Input, Tooltip, AutoComplete } from "antd";
import { InfoCircleOutlined, UserOutlined } from "@ant-design/icons";
import { MovieAPI } from "../MovieAPI";

//Function component Login
//Props:
//  userData - Stores user data, used to determine if the Login component displays ways to log in, or user information and Logout
//  onUserLoggedIn - Hook to handle passing user login event to App.js
function Login({ userData, onUserLoggedIn }) {
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

  const viewingDataContainer = {
    width: "100px",
    margin: "auto",
    clear: "both",
  };

  const filmIcon = {
    fontSize: "30px",
    width: "30px",
    float: "left",
  };

  const heartIcon = {
    fontSize: "25px",
    width: "30px",
    float: "left",
  };

  const viewingDataText = {
    fontSize: "15px",
    float: "left",
    paddingLeft: "10px",
  };

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
      <div style={{ color: "white" }}>
        <span>{userData.username}</span>
        <br />
        <div style={viewingDataContainer}>
          <span style={filmIcon} className="fas fa-film"></span>
          <span style={viewingDataText}>{userData.moviesSeen.length}</span>
        </div>
        <div style={viewingDataContainer}>
          <span style={heartIcon} className="fas fa-heart"></span>
          <span style={viewingDataText}>{userData.moviesToWatch.length}</span>
        </div>
        <br style={{ clear: "both" }} />
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
