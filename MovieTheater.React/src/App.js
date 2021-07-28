import "./App.css";
import { Layout } from "antd";
import CardList from "./CardList";
import SearchTools from "./SearchTools";
import MoviePage from "./MoviePage";
import Login from "./Login";
import { MovieAPI } from "./MovieAPI";
import { useState, useEffect } from "react";
import { BrowserRouter, Switch, Route } from "react-router-dom";

const { Sider, Content } = Layout;

function App() {
  const [userData, setUserData] = useState(null);
  const [search, setSearch] = useState({ count: 20 });
  const [isLoading, setIsLoading] = useState(true);

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
        console.log(responseData);
      });
  }

  return (
    <BrowserRouter>
      <div className="App" style={{ overflow: "hidden" }}>
        <Layout style={{ height: "100vh" }}>
          <Sider>
            <Login userData={userData} onUserLoggedIn={onUserLoggedIn} />
            <br />
            <SearchTools search={search} setSearch={setSearch} />
          </Sider>
          <Content
            style={{ height: "100%", overflowY: "auto", paddingRight: "10px" }}
          >
            {isLoading ? (
              <div>Loading...</div>
            ) : (
              <Switch>
                <Route path="/movie/:id" component={MoviePage} />
                <Route path="/">
                  <CardList
                    movieDataArray={movieDataArray}
                    userData={userData}
                    setUserData={setUserData}
                  ></CardList>
                </Route>
              </Switch>
            )}
          </Content>
        </Layout>
      </div>
    </BrowserRouter>
  );
}

export default App;
