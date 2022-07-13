import "./App.css";
import { Layout } from "antd";
import CardList from "./CardList";
import SearchTools from "./SearchTools";
import MoviePage from "./Pages/MoviePage";
import InsertPage from "./Pages/InsertPage";
import Login from "./Login";
import { MovieAPI } from "./MovieAPI";
import { useState, useEffect } from "react";
import { BrowserRouter, Switch, Route } from "react-router-dom";

const { Sider, Content } = Layout;

const storedUsername = window.localStorage.getItem("Username");

function App() {
  const [userData, setUserData] = useState(null);
  const [search, setSearch] = useState({ count: 20 });
  const [isLoading, setIsLoading] = useState(true);
  const [hasCheckedFirstLogin, setHasCheckedFirstLogin] = useState(false);

  const [movieDataArray, setMovieDataArray] = useState([]);
  useEffect(() => {
    setIsLoading(true);
    MovieAPI.getMovies(search)
      .then((response) => response.json())
      .then((responseData) => {
        setIsLoading(false);
        setMovieDataArray(responseData);
      });
  }, [search]);

  function onUserLoggedIn(username) {
    MovieAPI.loginUser(username)
      .then((response) => response.json())
      .then((responseData) => {
        setUserData(responseData);
        window.localStorage.setItem("Username", username);
        console.log(responseData);
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
        <Sider>
          <Login userData={userData} onUserLoggedIn={onUserLoggedIn} />
          <br />
          <SearchTools search={search} setSearch={setSearch} />
        </Sider>
        <Content style={{ height: "100%", overflowY: "auto", paddingRight: "10px" }}>
          {isLoading ? (
            <div>Loading...</div>
          ) : (
            <Switch>
              <Route path="/movie/:id" exact>
                <MoviePage userData={userData}></MoviePage>
              </Route>
              <Route path="/insert" exact>
                <InsertPage></InsertPage>
              </Route>
              <Route path="/">
                <CardList movieDataArray={movieDataArray} userData={userData} setUserData={setUserData}></CardList>
              </Route>
            </Switch>
          )}
        </Content>
      </Layout>
    </BrowserRouter>
  );
}

export default App;
