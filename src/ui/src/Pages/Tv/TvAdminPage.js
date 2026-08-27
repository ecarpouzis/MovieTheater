import { useCallback, useEffect, useState } from "react";
import { Button } from "antd";
import AdminShell from "../../admin/AdminShell";
import { AdminCard, AdminStats, NeedsAttention } from "../../admin/AdminOverview";
import { MovieAPI } from "../../MovieAPI";
import ChannelAdminModal from "./ChannelAdminModal";
import MyPlaylistsModal from "./MyPlaylistsModal";

// `/channels/admin?tab=` — the TV section's operator tools on the site's admin shell (R9 S6).
//
// Both of these tools are DIALOGS today (the channel editor used to open from inside TvPage, the
// playlist manager from the sider), and this pass deliberately does not rewrite either: the tab is
// a card that opens the tool it owns. What the tabs add is a URL — `?tab=channels` is now a link an
// operator can send — and the Overview report beside them.
//
// The route is `/channels/admin`, not `/tv/admin`: `/tv/:channelId?` is the screening room, so
// `/tv/admin` would be read as a channel called "admin" (and the bar hides itself on `/tv/`).

function ChannelsTab({ onChanged }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="adm-tab">
      <AdminCard
        title="Channels"
        description="Create, edit, enable and order the stations: the filter that fills each one, its scheduling strategy, its shelf and its rating ceiling. Saving regenerates the not-yet-aired schedule, so a change takes effect going forward, never retroactively."
        actions={<Button type="primary" onClick={() => setOpen(true)}>Open the channel editor</Button>}
      />
      <ChannelAdminModal open={open} onClose={() => setOpen(false)} onChanged={onChanged} />
    </div>
  );
}

function PlaylistsTab({ userData }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="adm-tab">
      <AdminCard
        title="Playlists"
        description="The personal channels: a playlist plays as a station on the guide. This is the same manager the sider offers every streaming account — it is here so the TV tools are in one place."
        actions={<Button type="primary" onClick={() => setOpen(true)}>Open my playlists</Button>}
      />
      <MyPlaylistsModal open={open} onClose={() => setOpen(false)} userData={userData} />
    </div>
  );
}

// The Overview is a REPORT off endpoints the section already serves:
//   /API/Channel/Admin/List   — every station with its filter, category and enabled flag
//   /API/Channel/Playlist/Mine — this account's playlist channels
// There is NO endpoint that reports a channel's POOL SIZE from the site, so "a station whose filter
// matches nothing" cannot be counted here; the report says that rather than guessing.
function TvOverviewTab({ hasPassword }) {
  const [channels, setChannels] = useState(null);
  const [playlists, setPlaylists] = useState(null);

  useEffect(() => {
    let alive = true;
    MovieAPI.getChannelAdminList()
      .then((r) => (r.ok ? r.json() : null))
      .then((v) => { if (alive) setChannels(Array.isArray(v) ? v : null); })
      .catch(() => { if (alive) setChannels(null); });
    if (hasPassword) {
      MovieAPI.getMyPlaylists()
        .then((r) => (r.ok ? r.json() : null))
        .then((v) => { if (alive) setPlaylists(Array.isArray(v) ? v : null); })
        .catch(() => { if (alive) setPlaylists(null); });
    }
    return () => { alive = false; };
  }, [hasPassword]);

  const list = channels ?? [];
  const disabled = channels ? list.filter((c) => !c.enabled).length : null;
  const noDescription = channels ? list.filter((c) => !c.description).length : null;
  const uncategorised = channels ? list.filter((c) => !c.category).length : null;

  return (
    <div className="adm-tab">
      <AdminStats
        stats={[
          { label: "Channels", value: channels ? list.length : null },
          { label: "On air", value: channels ? list.length - (disabled ?? 0) : null },
          { label: "Disabled", value: disabled },
          { label: "Catalog-authored", value: channels ? list.filter((c) => c.catalogKey).length : null },
          { label: "My playlists", value: playlists ? playlists.length : null },
        ]}
      />

      <NeedsAttention
        basePath="/channels/admin"
        description="Each row names the tab that fixes it."
        rows={[
          { key: "disabled", label: "Stations switched off", count: disabled, tab: "channels", tone: "warn", detail: "A disabled channel keeps its schedule but never appears on the guide." },
          { key: "nocat", label: "Stations with no shelf", count: uncategorised, tab: "channels", tone: "ok", detail: "The guide groups by category — an uncategorised station lands in the fallback shelf." },
          { key: "nodesc", label: "Stations with no description", count: noDescription, tab: "channels", tone: "ok", detail: "The guide's detail panel shows it when a viewer clicks the show." },
        ]}
      />

      <AdminCard
        title="What this page cannot report"
        description="A channel's POOL — how many titles its filter actually matches — is computed when the schedule is generated and is not served anywhere the site can read. An empty station therefore shows up as an empty guide, not as a number here. Profiling a filter is the channel-catalog CLI's job."
      />
    </div>
  );
}

export default function TvAdminPage({ userData }) {
  const canEdit = !!userData?.isAdmin || !!userData?.canEditMovies;
  const [beat, setBeat] = useState(0);
  const onChanged = useCallback(() => setBeat((b) => b + 1), []);
  void beat;
  return (
    <AdminShell
      section="tv"
      eyebrow="Channel administration"
      allowed={canEdit}
      deniedBody="The channel tools are for editors and administrators."
      tabs={[
        { key: "overview", label: "Overview", render: () => <TvOverviewTab hasPassword={!!userData?.hasPassword} /> },
        { key: "channels", label: "Channels", render: () => <ChannelsTab onChanged={onChanged} /> },
        { key: "playlists", label: "Playlists", render: () => <PlaylistsTab userData={userData} /> },
      ]}
    />
  );
}
