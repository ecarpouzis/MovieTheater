import { useEffect, useRef } from "react";
import { Button, notification } from "antd";
import { useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";

// Shown at most once per browser session: a nudge, not an alarm. The signature is the count, so a
// NEW proposal after the toast was dismissed earns one more toast and an unchanged queue does not.
const SESSION_KEY = "requests.attention.v1";

/**
 * The request queue's admin nudge (2026-10-06): when the background sweep has proposed matches for
 * open requests ("this looks like it was just added"), tell the admin ONCE per session, wherever
 * they are on the site, with a button to the queue. Non-admins render nothing and fetch nothing; on
 * the Requests page itself the banner does this job, so the toast stays quiet there.
 *
 * Deliberately NOT a poll — one Summary read per app load. The sweep runs half-hourly on the server
 * and the proposals wait; an admin who is about to see the banner anyway gains nothing from a timer.
 */
export default function RequestsAttentionToast({ userData }) {
  const history = useHistory();
  const location = useLocation();
  const askedRef = useRef(false);
  const isAdmin = !!userData?.isAdmin;
  const onQueue = location.pathname.startsWith("/requests");

  useEffect(() => {
    if (!isAdmin || onQueue || askedRef.current) return undefined;
    let cancelled = false;
    MovieAPI.getRequestsSummary()
      .then((r) => (r.ok ? r.json() : null))
      .then((s) => {
        if (cancelled) return;
        // Asked and answered — only now is this session done asking.
        askedRef.current = true;
        if (!s || !s.canResolve || !(s.needsConfirmation > 0)) return;
        const signature = String(s.needsConfirmation);
        let seen = null;
        try { seen = window.sessionStorage.getItem(SESSION_KEY); } catch { /* storage blocked */ }
        if (seen === signature) return;
        try { window.sessionStorage.setItem(SESSION_KEY, signature); } catch { /* storage blocked */ }
        const key = "requests-attention";
        const n = s.needsConfirmation;
        notification.open({
          key,
          title: n === 1 ? "A request looks like it's been added" : `${n} requests look like they've been added`,
          description: "The queue found matching titles in the library. Confirm them to close the requests.",
          placement: "bottomRight",
          duration: 12,
          actions: (
            <Button type="primary" size="small" onClick={() => { notification.destroy(key); history.push("/requests"); }}>
              Review
            </Button>
          ),
        });
      })
      .catch(() => { /* a failed nudge is no nudge */ });
    return () => { cancelled = true; };
  }, [isAdmin, onQueue, history]);

  return null;
}
