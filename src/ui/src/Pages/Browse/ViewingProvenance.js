import { EyeFilled, HeartFilled } from "@ant-design/icons";

/** "3 Aug 2026"; a row older than provenance (null date) reads "before Sep 2026". `short` drops the year. */
export function formatMarkDate(atUtc, style = "long") {
  if (!atUtc) return "before Sep 2026";
  const d = new Date(atUtc);
  if (Number.isNaN(d.getTime())) return "before Sep 2026";
  const opts = style === "short" ? { day: "numeric", month: "short" } : { day: "numeric", month: "short", year: "numeric" };
  return d.toLocaleDateString(undefined, opts);
}

function joinNames(names) {
  const n = names.filter(Boolean);
  if (n.length === 0) return "";
  if (n.length === 1) return n[0];
  return `${n.slice(0, -1).join(", ")} and ${n[n.length - 1]}`;
}

/**
 * The lines under the title sheet's Seen / Want pills (2026-09-05): the owner's marks with who placed
 * them and when — a Want placed by a friend IS the suggestion — and everybody else's marks on the
 * title. Read from `/API/ViewingDetail`; nothing drawn when the title carries no mark at all.
 * `scope.me` says whether the owner is the viewer ("you") or a friend (named); `viewer` is the viewer's
 * username, which turns a placer into "you". `onDismiss` un-wants a suggestion ("Not interested").
 */
export default function ViewingProvenance({ detail, scope, viewer, onDismiss }) {
  if (!detail) return null;
  const me = scope?.me !== false;
  const owner = me ? "you" : (scope.username || scope.forUser);
  const isViewer = (name) => !!viewer && !!name && name.toLowerCase() === viewer.toLowerCase();
  const isOwner = (name) => !!name && (me ? isViewer(name) : name.toLowerCase() === String(owner).toLowerCase());
  const lines = [];

  const want = detail.want;
  if (want) {
    const placer = want.byUsername && !isOwner(want.byUsername) ? (isViewer(want.byUsername) ? "you" : want.byUsername) : null;
    lines.push({
      key: "want", tone: "want", icon: <HeartFilled />,
      text: me
        ? (placer
          ? <>On your list · suggested by <b>{placer}</b>, {formatMarkDate(want.atUtc)}</>
          : <>On your list since · {formatMarkDate(want.atUtc)}</>)
        : (placer
          ? <><b>{owner}</b> wants to watch it · {placer === "you" ? "you suggested it" : <>suggested by <b>{placer}</b></>}, {formatMarkDate(want.atUtc)}</>
          : <><b>{owner}</b> wants to watch it · since {formatMarkDate(want.atUtc)}</>),
      action: me && placer && onDismiss ? <button type="button" className="prov-act" onClick={onDismiss}>Not interested</button> : null,
    });
  }

  const seen = detail.seen;
  if (seen) {
    const by = seen.byUsername && !isOwner(seen.byUsername) ? (isViewer(seen.byUsername) ? "you" : seen.byUsername) : null;
    lines.push({
      key: "seen", tone: "seen", icon: <EyeFilled />,
      text: me
        ? (by ? <>Seen · marked by <b>{by}</b> on your behalf, {formatMarkDate(seen.atUtc)}</> : <>Seen · {formatMarkDate(seen.atUtc)}</>)
        : (by ? <><b>{owner}</b> has seen it · marked by {by === "you" ? "you" : <b>{by}</b>}, {formatMarkDate(seen.atUtc)}</> : <><b>{owner}</b> has seen it · marked {formatMarkDate(seen.atUtc)}</>),
    });
  }

  const others = (detail.others ?? []).filter((o) => !(me && isViewer(o.username)));
  const seenBy = others.filter((o) => o.seen).map((o) => o.username);
  const wantBy = others.filter((o) => o.want).map((o) => o.username);
  if (seenBy.length || wantBy.length) {
    const parts = [];
    if (seenBy.length) parts.push(<><b>{joinNames(seenBy)}</b> {seenBy.length > 1 ? "have" : "has"} seen it</>);
    if (wantBy.length) parts.push(<><b>{joinNames(wantBy)}</b> {wantBy.length > 1 ? "want" : "wants"} to watch it</>);
    lines.push({
      key: "others", tone: seenBy.length ? "seen" : "want", icon: seenBy.length ? <EyeFilled /> : <HeartFilled />,
      text: parts.map((p, i) => <span key={i}>{i > 0 && " · "}{p}</span>),
    });
  }

  if (!lines.length) return null;
  return (
    <div className="prov">
      {lines.map((l) => (
        <span key={l.key} className="prov-line">
          <span className={`prov-ic prov-ic--${l.tone}`}>{l.icon}</span>
          <span className="prov-text">{l.text}</span>
          {l.action}
        </span>
      ))}
    </div>
  );
}
