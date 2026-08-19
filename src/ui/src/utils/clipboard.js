import { message } from "antd";

// The invite-link copy both room surfaces (watch party, arcade room) carried byte-for-byte:
// clipboard write with a toast, falling back to SHOWING the url when the clipboard API is blocked
// (http origin, permissions) so the user can copy it by hand. The music diag panel keeps its own
// execCommand fallback on purpose — the diag panel must work exactly where clipboard APIs don't.
export function copyLink(url, successMessage = "Invite link copied") {
  navigator.clipboard?.writeText(url).then(
    () => message.success(successMessage),
    () => message.info(url)
  );
}
