import { useEffect, useState } from "react";

import {
  diagEnabled, diagList, diagText, clearDiag, subscribeDiag, setDiagEnabled,
} from "./musicDiag";
import "./MusicDiagPanel.css";

// The reader for musicDiag's ring. Only mounts when ?diag=1 has been used, and shows a small
// floating tab above the play bar rather than anything that could get in the way of playback.
//
// Built for the phone case it exists to serve: the failure is minutes old by the time it's read, so
// the panel is a transcript with wall-clock times and gap markers, and the primary action is COPY —
// the log is only useful once it's out of the phone and in front of someone.

function gapOf(list, i) {
  if (i === 0) return 0;
  return list[i].at - list[i - 1].at;
}

function stamp(ms) {
  const d = new Date(ms);
  const p = (n, w = 2) => String(n).padStart(w, "0");
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`;
}

export default function MusicDiagPanel() {
  const [open, setOpen] = useState(false);
  const [, bump] = useState(0);
  const [copied, setCopied] = useState(false);

  useEffect(() => subscribeDiag(() => bump((n) => n + 1)), []);

  if (!diagEnabled()) return null;

  const list = diagList();

  async function copy() {
    const text = diagText();
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      // Clipboard API needs a secure context and permission; a hidden textarea always works.
      const ta = document.createElement("textarea");
      ta.value = text;
      ta.style.position = "fixed";
      ta.style.opacity = "0";
      document.body.appendChild(ta);
      ta.select();
      try { document.execCommand("copy"); } catch { /* nothing more to try */ }
      document.body.removeChild(ta);
    }
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  }

  if (!open) {
    return (
      <button className="music-diag-tab" onClick={() => setOpen(true)} title="Playback diagnostics">
        ⏺ {list.length}
      </button>
    );
  }

  return (
    <div className="music-diag">
      <div className="music-diag-head">
        <strong>Playback log</strong>
        <span className="music-diag-count">{list.length} events</span>
        <button onClick={copy}>{copied ? "Copied" : "Copy"}</button>
        <button onClick={clearDiag}>Clear</button>
        <button onClick={() => setOpen(false)}>Hide</button>
        <button
          onClick={() => { setDiagEnabled(false); setOpen(false); }}
          title="Stop recording and forget the flag"
        >
          Off
        </button>
      </div>
      <div className="music-diag-body">
        {list.length === 0 && <div className="music-diag-empty">Nothing recorded yet.</div>}
        {list.map((e, i) => {
          const gap = gapOf(list, i);
          return (
            <div key={e.id} className="music-diag-row">
              <span className="music-diag-time">{stamp(e.at)}</span>
              {/* A gap is the most informative thing in the whole log: it's the renderer frozen. */}
              {gap >= 1000 && <span className="music-diag-gap">+{(gap / 1000).toFixed(1)}s</span>}
              {e.hidden && <span className="music-diag-hidden">hidden</span>}
              <span className={`music-diag-event${e.event === "error" ? " music-diag-event--bad" : ""}`}>
                {e.event}
              </span>
              {e.data && <span className="music-diag-data">{JSON.stringify(e.data)}</span>}
            </div>
          );
        })}
      </div>
    </div>
  );
}
