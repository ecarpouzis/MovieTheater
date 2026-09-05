import { ArrowLeftOutlined } from "@ant-design/icons";
import { useHistory, useLocation } from "react-router-dom";
import "./ListsScopeBanner.css";

/**
 * One line above the results when the browse is on a FRIEND's lists (`?for=<username>`): whose
 * lists, what the controls now do, and the way back. Not a filter surface — the rail is the one
 * filter surface — just the scope, said once, where the reader is looking.
 */
export default function ListsScopeBanner({ scoped }) {
  const history = useHistory();
  const location = useLocation();
  if (!scoped || scoped.me) return null;
  const name = scoped.username || scoped.forUser;
  const back = () => {
    const params = new URLSearchParams(location.search);
    params.delete("for");
    params.delete("title");
    const search = params.toString();
    history.push({ pathname: "/", search: search ? `?${search}` : "" });
  };
  return (
    <div className="lists-scope" role="status">
      <span className="lists-scope-dot" aria-hidden="true">{(name || "?")[0].toUpperCase()}</span>
      <span className="lists-scope-text">
        <b>{name}’s lists.</b>
        {scoped.error
          ? " No one by that name — the lists are empty."
          : ` Seen and Want on these cards act on ${name}’s behalf — every mark is recorded as yours.`}
      </span>
      <button type="button" className="lists-scope-back" onClick={back}>
        <ArrowLeftOutlined /> Back to my lists
      </button>
    </div>
  );
}
