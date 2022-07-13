import { Layout } from "antd";

import SearchTools from "./SearchTools";
import Login from "./Login";

function NavBar({ search, setSearch, userData, onUserLoggedIn }) {
  return (
    <Layout.Sider>
      <Login userData={userData} onUserLoggedIn={onUserLoggedIn} />
      <br />
      <SearchTools search={search} setSearch={setSearch} />
    </Layout.Sider>
  );
}

export default NavBar;
