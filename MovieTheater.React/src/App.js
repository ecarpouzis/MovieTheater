import "./App.css";
import { Layout } from "antd";
import CardList from "./CardList";
import SearchTools from "./SearchTools";
import Login from "./Login";
const { Sider, Content } = Layout;

function App() {
  return (
    <div className="App">
      <Layout style={{ height: "100vh" }}>
        <Sider>
          <Login />
          <br />
          <SearchTools />
        </Sider>
        <Content style={{ height: "100%" }}>
          <CardList></CardList>
        </Content>
      </Layout>
    </div>
  );
}

export default App;
