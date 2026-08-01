import { useEffect, useRef, useState } from "react";
import { Modal, Typography } from "antd";
import { WarningOutlined } from "@ant-design/icons";
import { MovieAPI } from "../MovieAPI";

const { Text, Paragraph } = Typography;

// How often we ask the server. The watchdog only reports every 30 min, so polling faster buys
// nothing except load — 5 min is responsive enough to catch a revert within one worker recycle.
const POLL_MS = 5 * 60 * 1000;

/**
 * LOUD alarm for reverted patched binaries — and ONLY for that.
 *
 * WHY IT IS THIS AGGRESSIVE: we run ~11 binaries that are not what upstream ships (hand-built and
 * byte-patched arcade cores, cores pinned to one buildbot nightly, and 3 patched Jellyfin DLLs), and
 * both known revert mechanisms are COMPLETELY SILENT — the worker's cores.repo.sync reinstalls STOCK
 * over any core file that has gone missing, and any stock Jellyfin upgrade overwrites its 3 DLLs.
 * The previous guard only wrote to a log on Ziggy, which nobody reads, so a revert would surface
 * weeks later as "that bug we fixed is back". Hence: a blocking modal, not a toast.
 *
 * ⚠ THIS INTERRUPTS ONLY ON FINDINGS — i.e. only when a core actually shifted and a patch has to be
 * re-applied. It used to ALSO raise a sticky notification whenever the guard had not reported, and
 * that was noise, not signal: the report is held in memory per pod, so EVERY deploy reset it to
 * "never reported" and the very next admin page load got "Patched-binary guard is not reporting"
 * even though the watchdog on Ziggy was perfectly healthy and simply hadn't hit its next 30-minute
 * post. An alarm that fires after every deploy trains you to dismiss the one that matters.
 *
 * The guard's own liveness did not stop mattering, so it did not go away — it moved somewhere that
 * does not interrupt: the Admin modal shows the guard's state whenever an admin opens it. See
 * AdminModal.js. The server now also separates WARMING (just restarted, nothing is wrong) from
 * genuinely STALE (up longer than a full report window and still silent).
 *
 * Admin-only: the endpoint is admin-gated server-side and the alarm is actionable only by an admin,
 * so there is no reason to alarm guests about a core DLL.
 */
export default function PatchedArtifactAlarm({ userData }) {
  const [modalPayload, setModalPayload] = useState(null);
  // Remember what we already shouted about so a 5-minute poll doesn't stack duplicate popups.
  const shoutedRef = useRef({ signature: null });

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

      // data.stale / data.warming deliberately fall through WITHOUT interrupting: neither means a
      // binary changed, and the common one (warming) is just a fresh pod. Surfaced in the Admin modal.

      // No live findings: clear the latch so a genuinely NEW problem alarms again.
      shoutedRef.current.signature = null;
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
