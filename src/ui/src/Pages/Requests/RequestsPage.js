import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useHistory, useLocation } from "react-router-dom";
import { Button, Input, Modal, Table, Tooltip, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import useIsMobile from "../../hooks/useIsMobile";
import LoadFailure from "../../Components/LoadFailure";
import { REQUEST_SECTIONS, STATUS_LABEL, ago, catalogHref, sectionOf } from "./requestSections";
import "./RequestsPage.css";

/**
 * /requests (2026-10-06) — the communal wishlist for the LIBRARY: "I'm out and saw a movie / record /
 * boardgame / game we should have." Distinct from Want-to-Watch (what to watch out of what we have).
 *
 * Shape: a compose card at the top (section pills → title + year → the section's one disambiguating
 * field + a link → notes), then the whole queue from every user as a sortable table (cards on a
 * phone). The URL carries the view (`?status=open|closed|all`, `?mine=1`, `?section=`) so the rail's
 * index rows and this page agree, and a filter survives a reload.
 *
 * Resolution is loose and admin-confirmed. The server's matcher proposes a library row for a request
 * ("looks like: Dune (2021)"), on filing and on the half-hourly sweep; an admin confirms (closes it as
 * Added, linked to the row) or dismisses (remembered, not re-proposed). Nothing closes on its own.
 *
 * Writes are per-row and optimistic-free: every mutation takes the server's returned row and splices
 * it in, then fires `requests:changed` so the rail's counts refresh without a poll.
 */

const PAGE = 200;
const EMPTY = [];

// /API/Me carries the username, not an id — "mine" is a case-insensitive name match.
function isMine(r, me) {
  return !!me && !!r?.requestedBy?.username && r.requestedBy.username.toLowerCase() === me.toLowerCase();
}

function announce() {
  try { window.dispatchEvent(new Event("requests:changed")); } catch { /* no window */ }
}

async function bodyOf(resp) {
  try { return await resp.json(); } catch { return {}; }
}

// ── Compose ───────────────────────────────────────────────────────────────────────────────────────

function ComposeCard({ userData, onCreated, onDuplicate }) {
  const [section, setSection] = useState(REQUEST_SECTIONS[0].key);
  const [title, setTitle] = useState("");
  const [year, setYear] = useState("");
  const [detail, setDetail] = useState("");
  const [link, setLink] = useState("");
  const [notes, setNotes] = useState("");
  const [more, setMore] = useState(false);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState(null); // { kind: "dupe" | "have", request }
  const titleRef = useRef(null);
  const meta = sectionOf(section);
  const signedIn = !!userData;

  const reset = () => { setTitle(""); setYear(""); setDetail(""); setLink(""); setNotes(""); setMore(false); };

  const submit = async () => {
    if (!signedIn) return;
    const t = title.trim();
    if (!t) { message.warning("What's it called?"); titleRef.current?.focus(); return; }
    const y = year.trim() ? Number(year.trim()) : null;
    if (year.trim() && !Number.isInteger(y)) { message.warning("The year should be a number."); return; }
    setBusy(true);
    setNotice(null);
    try {
      const resp = await MovieAPI.createRequest({ section, title: t, year: y, detail: detail.trim() || null, link: link.trim() || null, notes: notes.trim() || null });
      const data = await bodyOf(resp);
      if (resp.status === 409 && data.duplicate) {
        setNotice({ kind: "dupe", request: data.duplicate });
        onDuplicate?.(data.duplicate);
        return;
      }
      if (!resp.ok) { message.error(data.message || "Couldn't file that request."); return; }
      onCreated(data);
      announce();
      reset();
      if (data.match) setNotice({ kind: "have", request: data });
      else message.success(`Added "${data.title}" to the list.`);
    } catch {
      message.error("Couldn't reach the server.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="rq-compose" aria-label="Request something">
      <div className="rq-compose__sections" role="group" aria-label="Which part of the site">
        {REQUEST_SECTIONS.map((s) => (
          <button
            key={s.key} type="button" aria-pressed={s.key === section}
            className={`rq-pill${s.key === section ? " on" : ""}`}
            style={{ "--pill-hue": s.hue }}
            onClick={() => setSection(s.key)}
          >
            <span className="rq-pill__dot" />{s.label}
          </button>
        ))}
      </div>

      <div className="rq-compose__row rq-compose__row--title">
        <Input
          ref={titleRef}
          className="rq-compose__title"
          size="large"
          placeholder={meta.titlePlaceholder || "Title"}
          value={title}
          maxLength={200}
          onChange={(e) => setTitle(e.target.value)}
          onPressEnter={submit}
          disabled={!signedIn || busy}
        />
        <Input
          className="rq-compose__year"
          size="large"
          placeholder="Year"
          aria-label="Year (optional)"
          inputMode="numeric"
          maxLength={4}
          value={year}
          onChange={(e) => setYear(e.target.value.replace(/[^0-9]/g, ""))}
          onPressEnter={submit}
          disabled={!signedIn || busy}
        />
      </div>

      <div className="rq-compose__row">
        <label className="rq-field">
          <span className="rq-field__label">{meta.detailLabel} <em className="rq-field__opt">optional</em></span>
          <Input value={detail} maxLength={200} placeholder={meta.detailHint} onChange={(e) => setDetail(e.target.value)} onPressEnter={submit} disabled={!signedIn || busy} />
        </label>
        {more ? (
          <label className="rq-field">
            <span className="rq-field__label">Link <em className="rq-field__opt">optional</em></span>
            <Input value={link} maxLength={500} placeholder="https:// — IMDb, BGG, a shop page…" inputMode="url" onChange={(e) => setLink(e.target.value)} onPressEnter={submit} disabled={!signedIn || busy} />
          </label>
        ) : (
          <button type="button" className="rq-linkish rq-compose__more" onClick={() => setMore(true)} disabled={!signedIn}>+ link or notes</button>
        )}
      </div>

      {more && (
        <div className="rq-compose__row">
          <label className="rq-field rq-field--wide">
            <span className="rq-field__label">Notes <em className="rq-field__opt">optional</em></span>
            <Input.TextArea value={notes} maxLength={1000} autoSize={{ minRows: 1, maxRows: 4 }} placeholder="The 4K cut, only the first three seasons, why it's worth having…" onChange={(e) => setNotes(e.target.value)} disabled={!signedIn || busy} />
          </label>
        </div>
      )}

      <div className="rq-compose__foot">
        {signedIn ? (
          <Button type="primary" size="large" onClick={submit} loading={busy} disabled={!title.trim()}>Add to the list</Button>
        ) : (
          <span className="rq-compose__signin">Sign in (the box in the rail) to add a request. Anyone can.</span>
        )}
        <span className="rq-compose__hint">Only the title is needed — fill in what you know. Everyone sees the list and can vote.</span>
      </div>

      {notice?.kind === "dupe" && (
        <div className="rq-notice rq-notice--dupe" role="status">
          <strong>{notice.request.requestedBy?.username ?? "Someone"}</strong> already asked for <em>{notice.request.title}</em>
          {notice.request.year ? ` (${notice.request.year})` : ""}. It's in the list below — give it a vote instead.
          <button type="button" className="rq-linkish" onClick={() => setNotice(null)}>Dismiss</button>
        </div>
      )}
      {notice?.kind === "have" && (
        <div className="rq-notice rq-notice--have" role="status">
          Filed — but we might already have this: <strong>{notice.request.match.title}</strong>.
          {catalogHref(notice.request.match.kind, notice.request.match.id) && (
            <> <Link to={catalogHref(notice.request.match.kind, notice.request.match.id)}>Take a look</Link>.</>
          )}
          {" "}If that's it, withdraw the request from the list below.
          <button type="button" className="rq-linkish" onClick={() => setNotice(null)}>Dismiss</button>
        </div>
      )}
    </section>
  );
}

// ── Row pieces shared by the table and the phone cards ────────────────────────────────────────────

function SectionTag({ section }) {
  const s = sectionOf(section);
  return <span className="rq-sec" style={{ "--pill-hue": s.hue }}><span className="rq-pill__dot" />{s.short}</span>;
}

function StatusTag({ r }) {
  return (
    <span className={`rq-status rq-status--${r.status}`} title={r.resolutionNote || undefined}>
      {STATUS_LABEL[r.status] ?? r.status}
      {r.status !== "open" && r.resolvedBy ? <span className="rq-status__by"> · {r.resolvedBy}</span> : null}
    </span>
  );
}

function MatchCallout({ r, canResolve, onConfirm, onDismiss, busy }) {
  if (!r.match || r.status !== "open") return null;
  const href = catalogHref(r.match.kind, r.match.id);
  return (
    <div className="rq-match" role="note">
      <span className="rq-match__lead">Looks like it's in:</span>{" "}
      {href ? <Link to={href} className="rq-match__title">{r.match.title}</Link> : <span className="rq-match__title">{r.match.title}</span>}
      {canResolve && (
        <span className="rq-match__acts">
          <Button size="small" type="primary" onClick={() => onConfirm(r)} loading={busy === `confirm:${r.id}`}>Confirm, it's added</Button>
          <Button size="small" onClick={() => onDismiss(r)} loading={busy === `dismiss:${r.id}`}>Not it</Button>
        </span>
      )}
    </div>
  );
}

function VoteButton({ r, me, onVote, busy }) {
  const own = isMine(r, me);
  const label = r.votes === 1 ? "1 vote" : `${r.votes} votes`;
  const disabled = !me || own || r.status !== "open" || busy === `vote:${r.id}`;
  return (
    <Tooltip title={!me ? "Sign in to vote" : own ? "Your own request — others can vote for it" : r.status !== "open" ? "Closed" : r.votedByMe ? "Take your vote back" : "Me too"}>
      <span className="rq-vote__wrap">
        <button
          type="button"
          className={`rq-vote${r.votedByMe ? " on" : ""}`}
          onClick={() => onVote(r)}
          disabled={disabled}
          aria-pressed={r.votedByMe}
          aria-label={`${label} for ${r.title}${r.votedByMe ? ", including yours" : ""}`}
        >
          <span className="rq-vote__glyph" aria-hidden="true">▲</span>
          <span className="rq-vote__n">{r.votes}</span>
        </button>
      </span>
    </Tooltip>
  );
}

function RowActions({ r, me, canResolve, act, busy }) {
  const own = isMine(r, me);
  const acts = [];
  if (r.status === "open") {
    if (canResolve) {
      acts.push(<Button key="added" size="small" onClick={() => act("fulfilled", r)} loading={busy === `fulfilled:${r.id}`}>Added</Button>);
      acts.push(<Button key="decline" size="small" onClick={() => act("declined", r)} loading={busy === `declined:${r.id}`}>Decline</Button>);
    }
    if (own) acts.push(<Button key="withdraw" size="small" onClick={() => act("withdrawn", r)} loading={busy === `withdrawn:${r.id}`}>Withdraw</Button>);
  } else {
    if (canResolve || (own && r.status === "withdrawn")) acts.push(<Button key="reopen" size="small" onClick={() => act("open", r)} loading={busy === `open:${r.id}`}>Reopen</Button>);
    if (canResolve) acts.push(<Button key="delete" size="small" danger onClick={() => act("delete", r)} loading={busy === `delete:${r.id}`}>Delete</Button>);
  }
  if (acts.length === 0) return null;
  return <div className="rq-acts">{acts}</div>;
}

function TitleCell({ r }) {
  return (
    <div className="rq-title">
      <div className="rq-title__line">
        <span className="rq-title__text">{r.title}</span>
        {r.year ? <span className="rq-title__year">{r.year}</span> : null}
        {r.link ? <a className="rq-title__link" href={r.link} target="_blank" rel="noopener noreferrer" title={r.link}>↗</a> : null}
      </div>
      {r.detail ? <div className="rq-title__detail">{r.detail}</div> : null}
      {r.notes ? <div className="rq-title__notes">{r.notes}</div> : null}
      {r.status === "fulfilled" && r.fulfilled && catalogHref(r.fulfilled.kind, r.fulfilled.id) ? (
        <Link className="rq-title__open" to={catalogHref(r.fulfilled.kind, r.fulfilled.id)}>Open it on the site</Link>
      ) : null}
      {r.status !== "open" && r.resolutionNote && !r.resolutionNote.startsWith("Matched: ") ? <div className="rq-title__note">“{r.resolutionNote}”</div> : null}
    </div>
  );
}

// ── The page ──────────────────────────────────────────────────────────────────────────────────────

export default function RequestsPage({ userData }) {
  const history = useHistory();
  const location = useLocation();
  const isMobile = useIsMobile();
  const me = userData?.username ?? null;
  const canResolve = !!userData?.isAdmin;

  const params = useMemo(() => new URLSearchParams(location.search), [location.search]);
  const status = params.get("status") ?? "open";
  const mineOnly = params.get("mine") === "1";
  const sectionFilter = params.get("section");

  const [rows, setRows] = useState(EMPTY);
  const [total, setTotal] = useState(0);
  const [nextBefore, setNextBefore] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [filter, setFilter] = useState("");
  const [busy, setBusy] = useState(null);
  const [sweeping, setSweeping] = useState(null); // { checked, proposed }
  const [summary, setSummary] = useState(null);
  const abortRef = useRef(null);

  const setParam = (key, value) => {
    const q = new URLSearchParams(location.search);
    if (value == null || value === "" || value === false) q.delete(key); else q.set(key, String(value));
    history.replace({ pathname: location.pathname, search: q.toString() ? `?${q}` : "" });
  };

  // Every superseded list read ABORTS (the site rule): a view change mid-flight stops the old request
  // on the server, and nothing it returns is written. Unmount aborts too.
  const load = useCallback(async (beforeId = null) => {
    abortRef.current?.abort?.();
    const ctl = typeof AbortController !== "undefined" ? new AbortController() : null;
    abortRef.current = ctl;
    const aborted = () => !!ctl?.signal.aborted;
    if (beforeId == null) setLoading(true);
    setError(null);
    try {
      const resp = await MovieAPI.getRequests({ status, section: sectionFilter, beforeId, limit: PAGE, signal: ctl?.signal });
      if (aborted()) return;
      if (resp.status === 401) { setRows(EMPTY); setTotal(0); setNextBefore(null); setError({ status: 401 }); return; }
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
      const data = await resp.json();
      if (aborted()) return;
      setRows((prev) => (beforeId == null ? data.requests : [...prev, ...data.requests]));
      setTotal(data.totalCount ?? 0);
      setNextBefore(data.nextBeforeId ?? null);
    } catch (e) {
      if (aborted()) return;
      setError(e);
    } finally {
      if (!aborted()) setLoading(false);
    }
  }, [status, sectionFilter]);

  useEffect(() => { if (userData) load(); else { setLoading(false); setRows(EMPTY); } }, [load, userData]);
  useEffect(() => () => abortRef.current?.abort?.(), []);

  const loadSummary = useCallback(() => {
    if (!userData) return;
    MovieAPI.getRequestsSummary().then((r) => (r.ok ? r.json() : null)).then((s) => { if (s) setSummary(s); }).catch(() => {});
  }, [userData]);
  useEffect(() => { loadSummary(); }, [loadSummary]);

  const fitsView = (r) => status === "all" || (status === "open" ? r.status === "open" : status === "closed" ? r.status !== "open" : r.status === status);

  const replaceRow = (next) => {
    // A row whose status left the current view drops out of it (open view, request fulfilled) and the
    // count follows; anything else is spliced in place. Decided here, outside the updater, because an
    // updater must be pure (StrictMode runs it twice).
    const stillFits = fitsView(next);
    const wasShown = rows.some((x) => x.id === next.id);
    if (wasShown && !stillFits) setTotal((t) => Math.max(0, t - 1));
    setRows((prev) => {
      const i = prev.findIndex((x) => x.id === next.id);
      if (i < 0) return stillFits ? [next, ...prev] : prev;
      if (!stillFits) return prev.filter((x) => x.id !== next.id);
      const copy = prev.slice(); copy[i] = next; return copy;
    });
    announce();
    loadSummary();
  };

  const run = async (key, call, onOk) => {
    setBusy(key);
    try {
      const resp = await call();
      const data = await bodyOf(resp);
      if (resp.status === 401) { message.warning("Sign in first."); return; }
      if (resp.status === 403) { message.warning("That's an admin action."); return; }
      if (!resp.ok) { message.error(data.message || "That didn't go through."); return; }
      onOk(data);
    } catch {
      message.error("Couldn't reach the server.");
    } finally {
      setBusy(null);
    }
  };

  const onVote = (r) => run(`vote:${r.id}`, () => MovieAPI.voteRequest(r.id), (d) => {
    setRows((prev) => prev.map((x) => (x.id === r.id ? { ...x, votes: d.votes, votedByMe: d.votedByMe } : x)));
  });
  const onConfirm = (r) => run(`confirm:${r.id}`, () => MovieAPI.confirmRequestMatch(r.id), (d) => { replaceRow(d); message.success(`"${r.title}" closed as added.`); });
  const onDismiss = (r) => run(`dismiss:${r.id}`, () => MovieAPI.dismissRequestMatch(r.id), replaceRow);

  const act = (what, r) => {
    if (what === "delete") {
      Modal.confirm({
        title: `Delete "${r.title}"?`,
        content: "This removes the request and its votes for everyone. Declining keeps it in the history instead.",
        okText: "Delete", okButtonProps: { danger: true },
        onOk: () => run(`delete:${r.id}`, () => MovieAPI.deleteRequest(r.id), () => {
          setRows((prev) => prev.filter((x) => x.id !== r.id)); setTotal((t) => Math.max(0, t - 1)); announce(); loadSummary();
        }),
      });
      return;
    }
    if (what === "declined") {
      let note = "";
      Modal.confirm({
        title: `Decline "${r.title}"?`,
        content: (
          <div className="rq-decline">
            <p>Say why, so {r.requestedBy?.username ?? "the requester"} knows. Optional.</p>
            <Input.TextArea autoSize={{ minRows: 2, maxRows: 4 }} maxLength={400} placeholder="Out of print · already covered by… · not for the site" onChange={(e) => { note = e.target.value; }} />
          </div>
        ),
        okText: "Decline",
        onOk: () => run(`declined:${r.id}`, () => MovieAPI.setRequestStatus(r.id, "declined", note.trim() || null), replaceRow),
      });
      return;
    }
    run(`${what}:${r.id}`, () => MovieAPI.setRequestStatus(r.id, what, null), (d) => {
      replaceRow(d);
      if (what === "fulfilled") message.success(`"${r.title}" closed as added.`);
    });
  };

  // The admin's hand pass of the matcher: bounded chunks, driven here to the end, totals accumulated,
  // a no-progress break (the house rule for every bulk job).
  const sweep = async () => {
    setSweeping({ checked: 0, proposed: 0 });
    let from = 0, checked = 0, proposed = 0, guard = 0, stopped = false;
    try {
      for (;;) {
        const resp = await MovieAPI.sweepRequests(from, 50);
        if (!resp.ok) { stopped = true; break; }
        const d = await resp.json();
        checked += d.checked; proposed += d.proposed;
        setSweeping({ checked, proposed });
        if (d.nextFrom == null || d.checked === 0 || ++guard > 40) break;
        from = d.nextFrom;
      }
      if (stopped) message.error(`Sweep stopped early after ${checked} requests.`);
      else message.success(proposed > 0 ? `Swept ${checked} open requests — ${proposed} new ${proposed === 1 ? "match" : "matches"} to confirm.` : `Swept ${checked} open requests — nothing new matched.`);
      load();
      loadSummary();
    } catch {
      message.error("Couldn't reach the server.");
    } finally {
      setSweeping(null);
    }
  };

  const visible = useMemo(() => {
    let list = rows;
    if (mineOnly && me) list = list.filter((r) => isMine(r, me));
    const f = filter.trim().toLowerCase();
    if (f) list = list.filter((r) => [r.title, r.detail, r.notes, r.requestedBy?.username, r.match?.title].some((x) => x && x.toLowerCase().includes(f)));
    return list;
  }, [rows, mineOnly, me, filter]);

  const needsConfirmation = canResolve ? (summary?.needsConfirmation ?? rows.filter((r) => r.status === "open" && r.match).length) : 0;

  const columns = [
    {
      title: "Title", dataIndex: "title", key: "title",
      sorter: (a, b) => a.title.localeCompare(b.title),
      render: (_, r) => (
        <>
          <TitleCell r={r} />
          <MatchCallout r={r} canResolve={canResolve} onConfirm={onConfirm} onDismiss={onDismiss} busy={busy} />
        </>
      ),
    },
    {
      title: "For", dataIndex: "section", key: "section", width: 128,
      sorter: (a, b) => a.section.localeCompare(b.section),
      filters: REQUEST_SECTIONS.map((s) => ({ text: s.label, value: s.key })),
      onFilter: (v, r) => r.section === v,
      render: (v) => <SectionTag section={v} />,
    },
    {
      title: "Asked by", dataIndex: ["requestedBy", "username"], key: "by", width: 140,
      sorter: (a, b) => (a.requestedBy?.username ?? "").localeCompare(b.requestedBy?.username ?? ""),
      render: (_, r) => <span className={`rq-by${isMine(r, me) ? " rq-by--me" : ""}`}>{r.requestedBy?.username ?? "—"}</span>,
    },
    {
      title: "When", dataIndex: "createdUtc", key: "when", width: 110,
      defaultSortOrder: "descend",
      sorter: (a, b) => new Date(a.createdUtc) - new Date(b.createdUtc),
      render: (v) => <time className="rq-when" dateTime={v} title={new Date(v).toLocaleString()}>{ago(v)}</time>,
    },
    {
      title: "Votes", dataIndex: "votes", key: "votes", width: 96, align: "center",
      sorter: (a, b) => a.votes - b.votes,
      render: (_, r) => <VoteButton r={r} me={me} onVote={onVote} busy={busy} />,
    },
    {
      title: "Status", dataIndex: "status", key: "status", width: 150,
      sorter: (a, b) => a.status.localeCompare(b.status),
      render: (_, r) => (
        <>
          <StatusTag r={r} />
          <RowActions r={r} me={me} canResolve={canResolve} act={act} busy={busy} />
        </>
      ),
    },
  ];

  const openCount = summary?.open ?? (status === "open" ? total : null);

  return (
    <div className="rq-page">
      <header className="rq-head">
        <div className="rq-head__line">
          <h1 className="rq-head__title">Requests</h1>
          {openCount != null && <span className="rq-head__count">{openCount} open</span>}
        </div>
        <p className="rq-head__blurb">
          Saw something we should have? Put it here — a movie, a record, a board game, a game, a book — and it goes on the
          list for everyone to see and vote on. When it lands on the site the request gets closed.
        </p>
      </header>

      <ComposeCard userData={userData} onCreated={(r) => { if (fitsView(r) && (!sectionFilter || r.section === sectionFilter)) { setRows((prev) => [r, ...prev]); setTotal((t) => t + 1); } loadSummary(); }} />

      {canResolve && needsConfirmation > 0 && (
        <div className="rq-banner" role="status">
          <strong>{needsConfirmation === 1 ? "One request looks like it's been added." : `${needsConfirmation} requests look like they've been added.`}</strong>{" "}
          Each one shows the library title it matched — confirm to close it, or say it's not the one.
        </div>
      )}

      <section className="rq-queue" aria-label="The request queue">
        <div className="rq-tools">
          <div className="rq-seg" role="group" aria-label="Which requests">
            {[["open", "Open"], ["closed", "Resolved"], ["all", "All"]].map(([k, l]) => (
              <button key={k} type="button" aria-pressed={status === k} className={`rq-seg__btn${status === k ? " on" : ""}`} onClick={() => setParam("status", k === "open" ? null : k)}>{l}</button>
            ))}
          </div>
          {me && (
            <button type="button" className={`rq-chip${mineOnly ? " on" : ""}`} aria-pressed={mineOnly} onClick={() => setParam("mine", mineOnly ? null : "1")}>Mine</button>
          )}
          {sectionFilter && (
            <button type="button" className="rq-chip on" onClick={() => setParam("section", null)} title="Clear the section filter">
              {sectionOf(sectionFilter).label} ✕
            </button>
          )}
          <Input allowClear className="rq-filter" placeholder="Filter the list…" value={filter} onChange={(e) => setFilter(e.target.value)} />
          <span className="rq-tools__count">{visible.length === rows.length ? `${total}` : `${visible.length} of ${total}`}</span>
          {canResolve && (
            <Tooltip title="Re-run the library match over every open request now (it also runs on its own every half hour).">
              <Button size="small" className="rq-tools__sweep" onClick={sweep} loading={!!sweeping}>{sweeping ? `Sweeping… ${sweeping.checked}` : "Check the library"}</Button>
            </Tooltip>
          )}
        </div>

        {!userData ? (
          <div className="rq-empty">Sign in to see the list.</div>
        ) : error && error.status !== 401 ? (
          <LoadFailure message="Couldn't load the request list." onRetry={() => load()} />
        ) : isMobile ? (
          <div className="rq-cards" aria-busy={loading}>
            {loading && rows.length === 0 && <div className="rq-empty">Loading…</div>}
            {!loading && visible.length === 0 && <div className="rq-empty">{rows.length === 0 ? "Nothing here yet — be the first." : "Nothing matches that filter."}</div>}
            {visible.map((r) => (
              <article key={r.id} className={`rq-card rq-card--${r.status}`}>
                <div className="rq-card__top">
                  <SectionTag section={r.section} />
                  <StatusTag r={r} />
                  <time className="rq-when" dateTime={r.createdUtc}>{ago(r.createdUtc)}</time>
                </div>
                <TitleCell r={r} />
                <div className="rq-card__by">asked by <span className={isMine(r, me) ? "rq-by--me" : ""}>{r.requestedBy?.username ?? "—"}</span></div>
                <MatchCallout r={r} canResolve={canResolve} onConfirm={onConfirm} onDismiss={onDismiss} busy={busy} />
                <div className="rq-card__foot">
                  <VoteButton r={r} me={me} onVote={onVote} busy={busy} />
                  <RowActions r={r} me={me} canResolve={canResolve} act={act} busy={busy} />
                </div>
              </article>
            ))}
          </div>
        ) : (
          <Table
            className="rq-table"
            rowKey="id"
            size="middle"
            loading={loading && rows.length === 0}
            dataSource={visible}
            columns={columns}
            pagination={false}
            rowClassName={(r) => `rq-row rq-row--${r.status}${r.match && r.status === "open" ? " rq-row--matched" : ""}`}
            locale={{ emptyText: rows.length === 0 ? "Nothing here yet — be the first." : "Nothing matches that filter." }}
          />
        )}

        {nextBefore != null && (
          <div className="rq-more">
            <Button onClick={() => load(nextBefore)} loading={loading}>Load older requests</Button>
          </div>
        )}
      </section>
    </div>
  );
}
