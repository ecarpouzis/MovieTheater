import logo from "./logo.svg";
import "./App.css";
import { Layout } from "antd";
import CardList from "./CardList";
const { Sider, Content } = Layout;

function App() {
  return (
    <div className="App">
      <Layout style={{ height: "100vh" }}>
        <Sider>Sider</Sider>
        <Content>
          <CardList></CardList>
        </Content>
      </Layout>
    </div>
  );
}

export default App;
