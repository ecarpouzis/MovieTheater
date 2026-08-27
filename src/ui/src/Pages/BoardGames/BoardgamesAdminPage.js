import { lazy } from "react";
import AdminShell from "../../admin/AdminShell";
import { AdminCard, AdminStats, NeedsAttention } from "../../admin/AdminOverview";
import useBoardgamesCatalog from "./useBoardgamesCatalog";

// `/boardgames/admin?tab=` — the collection's operator tools on the site's admin shell (R9 S6).
// `/boardgames/batchinsert` redirects into the Batch insert tab; the page itself is untouched.
//
// There is NO BGG-sync tab, deliberately: syncing is per game (`/API/SyncBoardgameFromBgg`,
// `/API/RematchBoardgame`) and its controls live in the game's own modal, where the operator can
// see what they are about to overwrite. A site-wide "re-sync everything" button would be a bulk
// write with no dry run, which this repo does not ship. The Overview names where the per-game
// controls are instead.
const BoardgameBatchInsertPage = lazy(() => import("./BoardgameBatchInsertPage"));

// The Overview is a REPORT off the catalog the browse itself reads (`/odata/Boardgames`, the
// anonymous unbounded query that IS this section's browse fetch) — no new API. Every count is a
// property of a row that query already returns.
function BoardgamesOverviewTab() {
  const catalog = useBoardgamesCatalog();
  const ready = !catalog.loading && !catalog.error;
  const games = catalog.games;
  const bases = ready ? games.filter((g) => (g.thingType ?? "").toLowerCase() !== "boardgameexpansion") : [];
  const expansions = ready ? games.length - bases.length : null;
  const orphanExpansions = ready ? games.filter((g) => (g.thingType ?? "").toLowerCase() === "boardgameexpansion" && !g.baseGameId).length : null;
  const noImage = ready ? games.filter((g) => !g.imageUrl).length : null;
  const noBgg = ready ? games.filter((g) => !g.bggThingId).length : null;
  const noRules = ready ? games.filter((g) => (g.rulesPdfUrls?.length ?? 0) === 0 && (g.howToPlayVideoUrls?.length ?? 0) === 0).length : null;
  const candidates = ready ? games.filter((g) => (g.rulesPdfCandidateUrls?.length ?? 0) > 0).length : null;

  return (
    <div className="adm-tab">
      <AdminStats
        stats={[
          { label: "Games", value: ready ? games.length : null },
          { label: "Base games", value: ready ? bases.length : null },
          { label: "Expansions", value: expansions },
          { label: "With rules", value: ready ? games.length - (noRules ?? 0) : null },
        ]}
      />

      <NeedsAttention
        basePath="/boardgames/admin"
        description="These are fixed in a game's own modal (open the game, then Edit) — the row says what to look for."
        rows={[
          { key: "catalog", label: "The collection did not answer", count: catalog.error ? 1 : 0, always: catalog.error, tone: "bad" },
          { key: "orphan", label: "Expansions with no base game linked", count: orphanExpansions, tone: "warn", detail: "Links self-heal in both directions on insert, sync and re-match — one that is still loose usually means the base game is not in the collection." },
          { key: "image", label: "Games with no box image", count: noImage, tone: "warn", detail: "Re-matching from BGG fetches one. Note: image writes only work against prod." },
          { key: "bgg", label: "Games with no BGG id", count: noBgg, tone: "warn", detail: "Nothing can be synced or re-matched until one is set." },
          { key: "candidates", label: "Games with unapproved rules-PDF candidates", count: candidates, tone: "ok", detail: "The discovery pass found something; a human still has to approve it into a slot." },
          { key: "rules", label: "Games with neither a rulebook nor a video", count: noRules, tone: "ok" },
        ]}
      />

      <AdminCard
        title="Where the BGG sync lives"
        description="Per game, in its modal: Re-match picks a different BGG thing, Sync re-pulls that thing's metadata, and both preserve the hand-set expansion groupings. There is no site-wide sync button on purpose — a bulk overwrite of the whole collection is not something this page should be able to do in one click."
      />
    </div>
  );
}

export default function BoardgamesAdminPage({ userData }) {
  const allowed = !!userData?.isAdmin || !!userData?.canEditMovies;
  return (
    <AdminShell
      section="boardgames"
      eyebrow="Collection administration"
      allowed={allowed}
      deniedBody="The collection tools are for editors and administrators."
      tabs={[
        { key: "overview", label: "Overview", render: () => <BoardgamesOverviewTab /> },
        { key: "batchinsert", label: "Batch insert", render: () => <BoardgameBatchInsertPage /> },
      ]}
    />
  );
}
