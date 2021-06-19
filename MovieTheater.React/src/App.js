import "./App.css";
import { Layout } from "antd";
import CardList from "./CardList";
import SearchTools from "./SearchTools";
import Login from "./Login";
import { MovieAPI } from "./MovieAPI";
import { useState, useEffect } from "react";

const { Sider, Content } = Layout;

function App() {
  const [count, setCount] = useState(30);
  const [isLoading, setIsLoading] = useState(true);
  const [movieDataArray, setMovieDataArray] = useState([]);
  useEffect(() => {
    setIsLoading(true);
    MovieAPI.getMovies(count)
      .then((response) => response.json())
      .then((responseData) => {
        setIsLoading(false);
        setMovieDataArray(responseData);
      });
  }, [count]);

  return (
    <div className="App" style={{ overflow: "hidden" }}>
      <Layout style={{ height: "100vh" }}>
        <Sider>
          <Login />
          <br />
          <SearchTools />
        </Sider>
        <Content
          style={{ height: "100%", overflowY: "auto", paddingRight: "10px" }}
        >
          {isLoading ? (
            <div>Loading...</div>
          ) : (
            <CardList movieDataArray={movieDataArray}></CardList>
          )}
        </Content>
      </Layout>
    </div>
  );
}

export default App;
