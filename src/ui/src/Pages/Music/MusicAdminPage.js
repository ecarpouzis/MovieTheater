import { useEffect, useState } from "react";
import AdminShell from "../../admin/AdminShell";
import { AdminCard, AdminStats, NeedsAttention } from "../../admin/AdminOverview";
import { MovieAPI } from "../../MovieAPI";
import { useMusicShelf } from "./useMusicShelf";

// `/music/admin?tab=` — Music has NO operator page today (every content job is a CLI: music-ingest,
// music-art, music-lyrics, and the library itself lives on the NAS), so its admin is an Overview
// REPORT and nothing else (R9 S6). That is the honest shape: inventing tabs for tools that do not
// exist would be worse than one page that says what the library holds and what is missing from it.
//
// Everything below comes from endpoints the section already serves:
//   /API/Music/Albums, /API/Music/Artists  — the shelf the browse itself reads (shared cache)
//   /API/Music/Capabilities                — whether streaming and the transcode/MSE lanes are configured
// Art coverage is computed from the shelf rows' own `hasArt` flag; nothing new is fetched for it.

function MusicOverviewTab() {
  const shelf = useMusicShelf("music");
  const [caps, setCaps] = useState(undefined);

  useEffect(() => {
    let alive = true;
    MovieAPI.getMusicCapabilities()
      .then((r) => (r.ok ? r.json() : null))
      .then((v) => alive && setCaps(v))
      .catch(() => alive && setCaps(null));
    return () => { alive = false; };
  }, []);

  const ready = !shelf.loading && !shelf.error;
  const albums = shelf.albums;
  const artists = shelf.artists;
  const artlessAlbums = ready ? albums.filter((a) => !a.hasArt).length : null;
  const artlessArtists = ready ? artists.filter((a) => !a.hasArt).length : null;
  const untagged = ready ? albums.filter((a) => !a.tag).length : null;
  const undated = ready ? albums.filter((a) => !a.year).length : null;

  return (
    <div className="adm-tab">
      <AdminStats
        stats={[
          { label: "Albums", value: ready ? albums.length : null },
          { label: "Artists", value: ready ? artists.length : null },
          { label: "Tracks", value: ready ? artists.reduce((n, a) => n + (a.trackCount ?? 0), 0) : null },
          { label: "Albums with art", value: ready ? albums.length - (artlessAlbums ?? 0) : null },
        ]}
      />

      <NeedsAttention
        basePath="/music/admin"
        description="Music's fixes are CLI jobs, so these rows name the job rather than a tab."
        rows={[
          { key: "shelf", label: "The music catalog did not answer", count: shelf.error ? 1 : 0, always: shelf.error, tone: "bad", detail: "Streaming needs a password session; a member without one sees an empty library." },
          { key: "art-albums", label: "Albums with no cover", count: artlessAlbums, tone: "warn", detail: "music-art fills these; a flat-folder album usually has no embedded picture to find." },
          { key: "art-artists", label: "Artists with no picture", count: artlessArtists, tone: "warn", detail: "An artist borrows a cover from one of its albums — an artist with none has no album with art either." },
          { key: "tag", label: "Albums with no quality tag", count: untagged, tone: "ok", detail: "[FLAC] / [V0] come from the folder name; an untagged album is simply un-labelled, not broken." },
          { key: "year", label: "Albums with no year", count: undated, tone: "ok" },
          { key: "stream", label: "Music streaming is not configured on this server", count: caps && !caps.streamingConfigured ? 1 : 0, always: !!caps && !caps.streamingConfigured, tone: "bad" },
        ]}
        clearText="Nothing the site can see needs attention."
      />

      <AdminCard
        title="The lanes this server advertises"
        description="A 'yes' is a statement about CONFIGURATION, not about the deployed gateway — the site deploys on push and the gateway does not, so a site ahead of its gateway advertises a lane that 404s. The player degrades quietly on that 404 by design."
      >
        <div className="adm-facts">
          <span>Streaming <code>{caps === undefined ? "…" : caps?.streamingConfigured ? "configured" : "absent"}</code></span>
          <span>Transcode <code>{caps === undefined ? "…" : caps?.transcodeEnabled ? "on" : "off"}</code></span>
          <span>fMP4 / MSE <code>{caps === undefined ? "…" : caps?.fmp4Enabled ? "on" : "off"}</code></span>
        </div>
      </AdminCard>

      <AdminCard
        title="Where the music tooling lives"
        description="Ingest, album art, lyrics and library moves are CLI jobs against the NAS (music-ingest, music-art, music-lyrics) — there is no site button for any of them, and this page deliberately does not pretend otherwise. Playback failures self-report: the player posts incidents rather than waiting to be caught live."
      />
    </div>
  );
}

export default function MusicAdminPage({ userData }) {
  const allowed = !!userData?.isAdmin;
  return (
    <AdminShell
      section="music"
      eyebrow="Music administration"
      allowed={allowed}
      deniedBody="The library report is for administrators."
      tabs={[{ key: "overview", label: "Overview", render: () => <MusicOverviewTab /> }]}
    />
  );
}
