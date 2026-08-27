import { useState, useEffect, useCallback } from "react";
import { useHistory } from "react-router-dom";
import ChannelGrid from "./ChannelGrid";
import GuideDetail from "./GuideDetail";
import { MovieAPI } from "../../MovieAPI";

/**
 * The /channels destination IS the cross-channel grid guide (the EPG). It sits in the content
 * area under the SectionBar (R9 S1c — it used to pin itself fixed over the whole window) with the
 * real-TV-guide move above it: click a show and its detail panel opens at the top (description,
 * ▶ Watch on that channel, Open title, ♥ the channel, up next); the channel button still tunes.
 */
export default function ChannelGuidePage({ userData, setUserData }) {
  const history = useHistory();
  const [channels, setChannels] = useState([]);
  const [selected, setSelected] = useState(null); // { channel, program, rowItems }

  useEffect(() => {
    let alive = true;
    MovieAPI.getChannelList()
      .then((r) => (r.ok ? r.json() : []))
      .then((c) => { if (alive) setChannels(c); })
      .catch(() => {});
    return () => { alive = false; };
  }, []);

  const onPickProgram = useCallback((channel, program, rowItems) => {
    setSelected((cur) => (cur && cur.channel.id === channel.id && cur.program.startUtc === program.startUtc ? null : { channel, program, rowItems }));
  }, []);

  return (
    <div className={`channel-guide-page${selected ? " channel-guide-page--detail" : ""}`}>
      {selected && (
        <GuideDetail
          channel={selected.channel}
          program={selected.program}
          rowItems={selected.rowItems}
          userData={userData}
          setUserData={setUserData}
          onClose={() => setSelected(null)}
        />
      )}
      <ChannelGrid
        open
        channels={channels}
        currentChannelId={null}
        onPick={(ch) => history.push("/tv/" + ch.id)}
        onPickProgram={onPickProgram}
        selectedKey={selected ? `${selected.channel.id}:${selected.program.startUtc}` : null}
        onClose={() => history.push("/")}
      />
    </div>
  );
}
