import { useState, useEffect, useCallback } from "react";
import { Button, Select, Tag, message, Spin } from "antd";
import { MovieAPI } from "../../MovieAPI";

// 3-letter ISO codes are what OpenSubtitles/Jellyfin's RemoteSearch expects.
const LANGS = [
  { value: "eng", label: "English" },
  { value: "spa", label: "Spanish" },
  { value: "fre", label: "French" },
  { value: "ger", label: "German" },
  { value: "ita", label: "Italian" },
  { value: "por", label: "Portuguese" },
  { value: "jpn", label: "Japanese" },
  { value: "kor", label: "Korean" },
  { value: "chi", label: "Chinese" },
];

// Subtitle picker for the movie edit modal. Lists the tracks currently attached, searches the
// configured Jellyfin subtitle provider (OpenSubtitles) for candidates — ranked so the "exact
// match" (made for this exact file, already in sync) comes first — lets the user download one,
// try it, and swap to another. All actions go through Jellyfin and land in its metadata dir, never
// the read-only NAS.
export default function SubtitlePicker({ movieId }) {
  const [synced, setSynced] = useState(true);
  const [current, setCurrent] = useState([]);
  const [loadingCurrent, setLoadingCurrent] = useState(true);
  const [language, setLanguage] = useState("eng");
  const [results, setResults] = useState(null);
  const [searching, setSearching] = useState(false);
  const [busyId, setBusyId] = useState(null);

  const loadCurrent = useCallback(async () => {
    setLoadingCurrent(true);
    try {
      const r = await MovieAPI.jellyfinSubtitlesList(movieId).then((x) => x.json());
      setSynced(r.synced);
      setCurrent(r.current || []);
    } catch {
      /* leave defaults */
    } finally {
      setLoadingCurrent(false);
    }
  }, [movieId]);

  useEffect(() => {
    loadCurrent();
  }, [loadCurrent]);

  async function search() {
    setSearching(true);
    setResults(null);
    try {
      const res = await MovieAPI.jellyfinSubtitlesSearch(movieId, language);
      const b = await res.json().catch(() => ({}));
      if (!res.ok) {
        message.error(b.message || "Subtitle search failed");
        return;
      }
      setResults(b.results || []);
      if ((b.results || []).length === 0) message.info("No subtitles found for that language.");
    } catch {
      message.error("Subtitle search failed");
    } finally {
      setSearching(false);
    }
  }

  async function download(sub) {
    setBusyId(sub.id);
    try {
      const res = await MovieAPI.jellyfinSubtitlesDownload(movieId, sub.id, language);
      const b = await res.json().catch(() => ({}));
      if (!res.ok || !b.downloaded) {
        message.error(b.message || "Download failed");
        return;
      }
      message.success(`Downloaded ${sub.name || "subtitle"}`);
      await loadCurrent();
    } catch {
      message.error("Download failed");
    } finally {
      setBusyId(null);
    }
  }

  async function remove(track) {
    const k = "rm" + track.index;
    setBusyId(k);
    try {
      const res = await MovieAPI.jellyfinSubtitlesDelete(movieId, track.index);
      const b = await res.json().catch(() => ({}));
      if (!res.ok || !b.deleted) {
        message.error(b.message || "Remove failed");
        return;
      }
      message.success("Subtitle removed");
      await loadCurrent();
    } catch {
      message.error("Remove failed");
    } finally {
      setBusyId(null);
    }
  }

  return (
    <div className="subtitle-picker">
      <span className="modal-label">Subtitles</span>

      {loadingCurrent ? (
        <Spin size="small" />
      ) : !synced ? (
        <div className="subtitle-note">Not synced to Jellyfin yet — run “Sync from Jellyfin”, then reopen this movie.</div>
      ) : (
        <>
          <div className="subtitle-current">
            {current.length === 0 ? (
              <span className="subtitle-note">No subtitle tracks attached.</span>
            ) : (
              current.map((t) => (
                <div className="subtitle-row" key={t.index}>
                  <Tag>{t.language || "?"}</Tag>
                  <span className="subtitle-name">{t.title || t.codec || "subtitle"}</span>
                  {t.external ? <Tag color="blue">downloaded</Tag> : <Tag>embedded</Tag>}
                  {t.external && (
                    <Button size="small" danger loading={busyId === "rm" + t.index} onClick={() => remove(t)}>
                      Remove
                    </Button>
                  )}
                </div>
              ))
            )}
          </div>

          <div className="subtitle-search-bar">
            <Select size="small" value={language} onChange={setLanguage} options={LANGS} style={{ width: 130 }} />
            <Button size="small" type="primary" loading={searching} onClick={search}>
              Search subtitles online
            </Button>
          </div>

          {results && results.length > 0 && (
            <div className="subtitle-results">
              {results.map((s) => (
                <div className="subtitle-row" key={s.id}>
                  {s.hashMatch ? (
                    <Tag color="green" title="Uploaded for this exact file — already in sync">✓ exact match</Tag>
                  ) : null}
                  {s.trusted ? <Tag color="gold" title="From a trusted uploader">trusted</Tag> : null}
                  {s.hearingImpaired ? <Tag title="Hearing-impaired (SDH)">SDH</Tag> : null}
                  {s.aiTranslated ? <Tag color="orange" title="Machine/AI translated — lower quality">AI</Tag> : null}
                  <span className="subtitle-name" title={s.comment || s.name}>{s.name || "(unnamed)"}</span>
                  <span className="subtitle-meta">
                    {s.provider}
                    {s.downloads != null ? ` · ${Number(s.downloads).toLocaleString()} dl` : ""}
                    {s.rating ? ` · ★${s.rating}` : ""}
                  </span>
                  <Button size="small" loading={busyId === s.id} onClick={() => download(s)}>
                    Use this
                  </Button>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}
