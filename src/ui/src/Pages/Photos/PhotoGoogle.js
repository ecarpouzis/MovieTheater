import { useCallback, useEffect, useState } from "react";
import { Spin, message } from "antd";
import { MovieAPI } from "../../MovieAPI";

// The Google mesh section of the Review tab (docs/photos-plan.md §2.10).
//
// The Photos Library API lost third-party read access in 2025, so the mesh runs as a CLI pass over a
// downloaded Takeout archive. This surface is the human half of it:
//
//  · how the archive's items landed against the library, and by which matching rung;
//  · what Google's sidecars disagree with our metadata about — both the ones that WON over a weaker
//    local source and were written (flag-but-write), and the ones that lost to a camera and were not;
//  · the Google-only list: pictures the archive holds and the library does not, thumbnailed FROM THE
//    ARCHIVE, each of which a family member can ignore.
//
// Ignoring is the only write here, and it is deliberately the only one: bringing a Google-only photo
// down is a file copy onto the collection host, which is a separate, opt-in, operator-run command.

const RUNG_LABELS = {
  "name+size": "name and size",
  sha256: "content hash",
  phash: "pixel similarity",
};

const FIELD_LABELS = {
  takenAt: "Google's date lost to a stronger local source",
  "takenAt-overwritten": "Google's date replaced a weaker local guess",
  gps: "Google's coordinates differ from ours",
  locationLabel: "Google's place name differs from ours",
};

function formatDate(value) {
  if (!value) return "No date";
  return String(value).replace("T", " ").slice(0, 16) + " UTC";
}

export default function PhotoGoogle() {
  const [stats, setStats] = useState(null);
  const [list, setList] = useState(null);
  const [includeIgnored, setIncludeIgnored] = useState(false);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const response = await MovieAPI.getPhotosGoogleMesh();
      setStats(response.ok ? await response.json() : { ran: false });
    } catch {
      setStats({ ran: false });
    }
    try {
      const response = await MovieAPI.getPhotosGoogleOnly({ includeIgnored });
      setList(response.ok ? await response.json() : { items: [], total: 0 });
    } catch {
      setList({ items: [], total: 0 });
    }
  }, [includeIgnored]);

  useEffect(() => {
    load();
  }, [load]);

  const setIgnored = async (id, ignored) => {
    setBusy(true);
    try {
      const response = await MovieAPI.ignorePhotosGoogleItems([id], ignored);
      if (!response.ok) {
        message.error("Could not record that.");
        return;
      }
      await load();
    } finally {
      setBusy(false);
    }
  };

  if (!stats) return <Spin />;

  if (!stats.ran) {
    return (
      <section className="photo-google">
        <h2 className="photos-panel-head">Google Photos</h2>
        <p className="photos-note">
          No Takeout archive has been meshed on this database yet. Google's Library API no longer
          grants third-party read access, so the lane is an exported archive: run
          <code> photos-google-mesh </code> against one and its items appear here.
        </p>
      </section>
    );
  }

  return (
    <section className="photo-google">
      <h2 className="photos-panel-head">Google Photos</h2>

      <ul className="photo-google-stats">
        <li>
          <span className="photo-google-stat">{(stats.total || 0).toLocaleString()}</span>
          <span>archive items</span>
        </li>
        <li>
          <span className="photo-google-stat">{(stats.matched || 0).toLocaleString()}</span>
          <span>already in the library</span>
        </li>
        <li>
          <span className="photo-google-stat">{(stats.googleOnly || 0).toLocaleString()}</span>
          <span>Google only</span>
        </li>
        <li>
          <span className="photo-google-stat">{(stats.ignored || 0).toLocaleString()}</span>
          <span>ignored</span>
        </li>
        {stats.downloaded > 0 && (
          <li>
            <span className="photo-google-stat">{stats.downloaded.toLocaleString()}</span>
            <span>downloaded</span>
          </li>
        )}
      </ul>

      {!stats.drained && (
        <p className="photos-note">
          {/* §2.10's drain guard, said out loud: a half-matched archive would offer to download
              photographs the library already holds, so the download lane refuses until this is 0. */}
          {(stats.pending || 0).toLocaleString()} item{stats.pending === 1 ? "" : "s"} have not been
          matched yet, so this list is incomplete and the download lane will refuse to run.
        </p>
      )}

      {stats.byMethod?.length > 0 && (
        <ul className="photo-review-rules">
          {stats.byMethod.map((rung) => (
            <li key={rung.method}>
              <span className="photo-review-rule">matched by {RUNG_LABELS[rung.method] || rung.method}</span>
              <span className="photo-review-count">{rung.count.toLocaleString()}</span>
            </li>
          ))}
        </ul>
      )}

      {stats.disagreements?.length > 0 && (
        <>
          <h3 className="photo-google-subhead">
            {stats.disagreeingItems.toLocaleString()} item{stats.disagreeingItems === 1 ? "" : "s"} where
            Google disagrees with us
          </h3>
          <ul className="photo-review-rules">
            {stats.disagreements.map((row) => (
              <li key={row.field}>
                <span className="photo-review-rule">{FIELD_LABELS[row.field] || row.field}</span>
                <span className="photo-review-count">{row.count.toLocaleString()}</span>
              </li>
            ))}
          </ul>
        </>
      )}

      <div className="photo-google-listhead">
        <h3 className="photo-google-subhead">Google only</h3>
        <label className="photo-google-toggle">
          <input
            type="checkbox"
            checked={includeIgnored}
            onChange={(e) => setIncludeIgnored(e.target.checked)}
          />
          Show ignored
        </label>
      </div>

      {!list && <Spin />}
      {list && list.items.length === 0 && (
        <p className="photos-note">
          Nothing is Google only — every item in the archive was found in the library.
        </p>
      )}

      {list && list.items.length > 0 && (
        <>
          <ul className="photo-google-grid">
            {list.items.map((item) => (
              <li className={"photo-google-card" + (item.ignored ? " is-ignored" : "")} key={item.id}>
                {/* A missing thumb is a real state, not a broken image: the mesh's thumb pass may not
                    have run, or the archive may hold a format this build cannot decode. */}
                {item.gridUrl ? (
                  <img src={item.gridUrl} alt="" loading="lazy" />
                ) : (
                  <div className="photo-google-placeholder" />
                )}
                <div className="photo-google-meta">
                  <span className="photo-google-name" title={item.archivePath || item.fileName}>
                    {item.fileName}
                  </span>
                  <span className="photo-google-date">{formatDate(item.takenAtUtc)}</span>
                  {item.description && <span className="photo-google-desc">{item.description}</span>}
                </div>
                <button
                  type="button"
                  className="photos-button"
                  disabled={busy}
                  onClick={() => setIgnored(item.id, !item.ignored)}
                >
                  {item.ignored ? "Un-ignore" : "Ignore"}
                </button>
              </li>
            ))}
          </ul>
          {list.total > list.items.length && (
            <p className="photos-note">
              Showing {list.items.length.toLocaleString()} of {list.total.toLocaleString()}.
            </p>
          )}
        </>
      )}
    </section>
  );
}
