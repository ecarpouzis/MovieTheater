import { Layout } from "antd";

import SearchTools from "./SearchTools";
import Login from "./Login";

function NavBar({
  search,
  setSearch,
  resetSearch,
  userData,
  setUserData,
  onUserLoggedIn,
  titleSearch,
  actorSearch,
  moviesSeenSearch,
  moviesWantToWatchSearch,
}) {
  return (
    <Layout.Sider>
      <Login
        userData={userData}
        setUserData={setUserData}
        onUserLoggedIn={onUserLoggedIn}
        moviesSeenSearch={moviesSeenSearch}
        moviesWantToWatchSearch={moviesWantToWatchSearch}
      />
      <br />
      <SearchTools search={search} setSearch={setSearch} resetSearch={resetSearch} titleSearch={titleSearch} actorSearch={actorSearch} />
    </Layout.Sider>
  );
}

export default NavBar;
