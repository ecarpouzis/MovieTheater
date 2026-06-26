import { useState, useEffect } from "react";
import { useHistory } from "react-router-dom";
import ChannelGrid from "./ChannelGrid";
import { MovieAPI } from "../../MovieAPI";

/**
 * The /channels destination IS the cross-channel grid guide (the EPG) — it replaced the poster browser.
 * Fetches the channel list and renders the guide full-screen; picking a channel tunes it, closing
 * returns home.
 */
export default function ChannelGuidePage() {
  const history = useHistory();
  const [channels, setChannels] = useState([]);

  useEffect(() => {
    let alive = true;
    MovieAPI.getChannelList()
      .then((r) => (r.ok ? r.json() : []))
      .then((c) => { if (alive) setChannels(c); })
      .catch(() => {});
    return () => { alive = false; };
  }, []);

  return (
    <div className="channel-guide-page">
      <ChannelGrid
        open
        channels={channels}
        currentChannelId={null}
        onPick={(ch) => history.push("/tv/" + ch.id)}
        onClose={() => history.push("/")}
      />
    </div>
  );
}
