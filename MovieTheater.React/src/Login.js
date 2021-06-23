import { useState, useEffect } from "react";
import { Button, Input, Tooltip, AutoComplete } from "antd";
import { InfoCircleOutlined, UserOutlined } from "@ant-design/icons";
import { MovieAPI } from "./MovieAPI";

function Login({ username, onUserLoggedIn }) {
  const handleKeyPress = (ev) => {
    console.log("handleKeyPress", ev);
  };

  const onSelect = (value) => {
    onUserLoggedIn(value);
  };

  const [userlist, setUserlist] = useState([]);
  const [filteredUserList, setFilteredUserList] = useState([]);

  const handleSearch = (value) => {
    var filteredList = userlist.filter((e) => {
      return e.value.toLowerCase().includes(value.toLowerCase());
    });
    setFilteredUserList(filteredList);
  };

  useEffect(
    () =>
      MovieAPI.getUsers()
        .then((response) => {
          console.log(response);
          return response.json();
        })
        .then((responseData) => {
          const responseDataMap = responseData.map((x) => ({
            value: x,
          }));

          setUserlist(responseDataMap);
          setFilteredUserList(responseDataMap);
        }),
    []
  );

  const getLoginTools = () => (
    <div id="LoginContainer" style={{ color: "white" }}>
      <span style={{ fontWeight: "bold", fontSize: "18px" }}>LOG IN</span>
      <br />
      <br />
      <AutoComplete
        options={filteredUserList}
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
            onKeyPress={handleKeyPress}
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
          >
            {">"}
          </Button>
        </div>
      </AutoComplete>
    </div>
  );

  function getLoggedInDisplay(username) {
    return <span>{username}</span>;
  }

  if (username) {
    return getLoggedInDisplay(username);
  } else {
    return getLoginTools();
  }
}
export default Login;
