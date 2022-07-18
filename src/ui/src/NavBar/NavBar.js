import { Layout } from "antd";

import SearchTools from "./SearchTools";
import Login from "./Login";

function NavBar({ search, setSearch, resetSearch, userData, onUserLoggedIn }) {
  return (
    <Layout.Sider>
      <Login userData={userData} onUserLoggedIn={onUserLoggedIn} />
      <br />
      <SearchTools search={search} setSearch={setSearch} resetSearch={resetSearch} />
    </Layout.Sider>
  );
}

export default NavBar;
