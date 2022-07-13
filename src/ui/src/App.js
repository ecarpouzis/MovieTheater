import { Layout } from "antd";
import { MovieAPI } from "./MovieAPI";
import { useState } from "react";
import { BrowserRouter, Switch, Route } from "react-router-dom";

import NavBar from "./NavBar/NavBar";
import Browse from "./Pages/Browse/Browse";
import MoviePage from "./Pages/MoviePage";
import InsertPage from "./Pages/InsertPage";

const storedUsername = window.localStorage.getItem("Username");

function App() {
  const [userData, setUserData] = useState(null);
  const [search, setSearch] = useState({ count: 20 });
  const [hasCheckedFirstLogin, setHasCheckedFirstLogin] = useState(false);

  function onUserLoggedIn(username) {
    MovieAPI.loginUser(username)
      .then((response) => response.json())
      .then((responseData) => {
        setUserData(responseData);
        window.localStorage.setItem("Username", username);
      });
  }

  if (!hasCheckedFirstLogin) {
    setHasCheckedFirstLogin(true);
    if (storedUsername) {
      onUserLoggedIn(storedUsername);
    }
  }

  return (
    <BrowserRouter>
      <Layout style={{ height: "100vh", overflow: "hidden" }}>
        <NavBar search={search} setSearch={setSearch} userData={userData} onUserLoggedIn={onUserLoggedIn} />
        <Layout.Content style={{ height: "100%", overflowY: "auto", paddingRight: "10px" }}>
          <Switch>
            <Route path="/movie/:id" exact>
              <MoviePage userData={userData} />
            </Route>
            <Route path="/insert" exact>
              <InsertPage />
            </Route>
            <Route path="/">
              <Browse search={search} userData={userData} setUserData={setUserData} />
            </Route>
          </Switch>
        </Layout.Content>
      </Layout>
    </BrowserRouter>
  );
}

export default App;
