import { useEffect, useRef, useState } from "react";
import { Modal, notification, Typography } from "antd";
import { WarningOutlined } from "@ant-design/icons";
import { MovieAPI } from "../MovieAPI";

const { Text, Paragraph } = Typography;

// How often we ask the server. The watchdog only reports every 30 min, so polling faster buys
// nothing except load — 5 min is responsive enough to catch a revert within one worker recycle.
const POLL_MS = 5 * 60 * 1000;

// Deliberately NOT auto-dismissing. Every one of these means a binary we hand-built is no longer
// running, which stays true until a human acts, so a toast that fades away would be worse than
// useless: it would let a revert pass by unnoticed while looking like it was reported.
const STICKY = 0;

/**
 * LOUD alarm for reverted patched binaries.
 *
 * WHY IT IS THIS AGGRESSIVE: we run ~11 binaries that are not what upstream ships (hand-built and
 * byte-patched arcade cores, cores pinned to one buildbot nightly, and 3 patched Jellyfin DLLs), and
 * both known revert mechanisms are COMPLETELY SILENT — the worker's cores.repo.sync reinstalls STOCK
 * over any core file that has gone missing, and any stock Jellyfin upgrade overwrites its 3 DLLs.
 * The previous guard only wrote to a log on Ziggy, which nobody reads, so a revert would surface
 * weeks later as "that bug we fixed is back". Hence: a blocking modal, not a toast.
 *
 * Three states are treated as alarms, and the third is the one that usually gets missed:
 *   - findings    : something was reverted / replaced / differs between workers -> MODAL
 *   - stale       : no report in ~95 min, i.e. the WATCHDOG is dead -> sticky notification
 *   - never       : no report since this pod started -> same as stale (absence of evidence is not
 *                   evidence of health, so it must never render as a green light)
 *
 * Admin-only: the endpoint is admin-gated server-side and the alarm is actionable only by an admin,
 * so there is no reason to alarm guests about a core DLL.
 */
export default function PatchedArtifactAlarm({ userData }) {
  const [modalPayload, setModalPayload] = useState(null);
  // Remember what we already shouted about so a 5-minute poll doesn't stack duplicate popups.
  const shoutedRef = useRef({ signature: null, staleShown: false });

  const isAdmin = !!userData?.isAdmin;

  useEffect(() => {
    if (!isAdmin) return undefined;

    let cancelled = false;

    async function check() {
      let data;
      try {
        const resp = await MovieAPI.adminGetPatchedArtifacts();
        if (!resp.ok) return; // 403 for a non-password session etc. — not our problem to report
        data = await resp.json();
      } catch {
        return; // network blip; the next poll will retry. Never alarm on our own fetch failing.
      }
      if (cancelled || !data) return;

      let findings = [];
      try {
        findings = data.payloadJson ? JSON.parse(data.payloadJson).findings ?? [] : [];
      } catch {
        findings = [];
      }

      if (!data.ok && data.reported) {
        // Signature on the finding set, not a counter: re-alarm when the SET changes (a new artifact
        // broke) but stay quiet while the same unresolved problem is re-reported every 30 min.
        const signature = findings
          .map((f) => `${f.status}:${f.id}:${f.path}`)
          .sort()
          .join("|");
        if (signature !== shoutedRef.current.signature) {
          shoutedRef.current.signature = signature;
          setModalPayload({ findings, receivedUtc: data.receivedUtc });
        }
        return;
      }

      if (data.stale) {
        if (!shoutedRef.current.staleShown) {
          shoutedRef.current.staleShown = true;
          notification.warning({
            key: "patched-artifact-stale",
            message: "Patched-binary guard is not reporting",
            duration: STICKY,
            icon: <WarningOutlined style={{ color: "#faad14" }} />,
            description: data.reported
              ? `Last report was ${data.ageMinutes} min ago (expected every 30). The arcade watchdog on Ziggy may be dead — until it reports again we do NOT know whether our patched cores and Jellyfin DLLs are intact.`
              : "No report since the site started. The arcade watchdog on Ziggy has not checked in, so the state of our patched cores and Jellyfin DLLs is UNKNOWN.",
          });
        }
        return;
      }

      // Healthy: clear the latches so a future problem alarms again.
      shoutedRef.current.signature = null;
      if (shoutedRef.current.staleShown) {
        shoutedRef.current.staleShown = false;
        notification.destroy("patched-artifact-stale");
      }
    }

    check();
    const timer = setInterval(check, POLL_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [isAdmin]);

  if (!modalPayload) return null;

  const worst = modalPayload.findings.some((f) => f.status === "MISSING");

  return (
    <Modal
      open
      title={
        <span>
          <WarningOutlined style={{ color: "#cf1322", marginRight: 8 }} />
          Patched binary {worst ? "MISSING" : "changed"} — a hand-built patch is not running
        </span>
      }
      onCancel={() => setModalPayload(null)}
      onOk={() => setModalPayload(null)}
      okText="Acknowledge"
      cancelButtonProps={{ style: { display: "none" } }}
      width={760}
    >
      <Paragraph>
        One or more binaries we patched ourselves no longer match the recorded manifest. Until this is
        fixed, the fixes they carry are <Text strong>not in effect</Text>.
      </Paragraph>
      {worst && (
        <Paragraph type="danger">
          A <Text strong>MISSING</Text> core is the urgent case: the worker's core sync installs the
          upstream <Text strong>stock</Text> build over any absent file on its next start, silently
          de-patching the fleet.
        </Paragraph>
      )}
      <ul style={{ maxHeight: 260, overflowY: "auto" }}>
        {modalPayload.findings.map((f, i) => (
          <li key={i} style={{ marginBottom: 6 }}>
            <Text code>{f.status}</Text> <Text strong>{f.id}</Text>
            {f.stockName && f.status === "MISSING" && (
              <Text type="danger"> — stock name, will be replaced with stock</Text>
            )}
            <div>
              <Text type="secondary" style={{ fontSize: 12, wordBreak: "break-all" }}>
                {f.path}
              </Text>
            </div>
            {f.detail && (
              <div>
                <Text type="secondary" style={{ fontSize: 12 }}>
                  {f.detail}
                </Text>
              </div>
            )}
          </li>
        ))}
      </ul>
      <Paragraph style={{ marginBottom: 0 }}>
        <Text type="secondary" style={{ fontSize: 12 }}>
          Fix on Ziggy: <Text code>scripts\verify-patched-artifacts.ps1</Text> — add{" "}
          <Text code>-Restore</Text> if this was a revert (restores vaulted bytes, then recycle the
          workers / restart Jellyfin), or <Text code>-Snapshot</Text> if you rebuilt intentionally.
        </Text>
      </Paragraph>
    </Modal>
  );
}
