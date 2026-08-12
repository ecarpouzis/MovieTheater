# Immich sidecar — deployment runbook

**Status: RUNBOOK. Nothing in this document runs automatically.** A human executes it, once, on the
LAN-only host that sits beside the collection. No build, test, deploy or CLI in this repository ever
starts, upgrades or reaches a live Immich on its own.

Companion to `docs/photos-plan.md` §2.4, which is where the *why* lives. This is the *how*.

---

## What this is, and what it is not

Immich is used here the way Jellyfin is used: **headless plumbing behind the site, never a user
surface.** It looks at the collection, computes things a CPU is good at — EXIF, offline reverse
geocoding, face detection and clustering, CLIP embeddings, duplicate candidates — and our
`photos-sync-immich` CLI pulls those out as **suggestions**. Our database owns all truth.

Three consequences that decide every step below:

- **It is disposable.** The container and its database can be deleted and rebuilt at any time. Our
  rows reference it only by ids that are re-derivable from paths, so nothing of ours is lost. The
  album is fully usable with it gone: people, tagging, the tag queue and the date editor all work by
  hand, which is how they were built and how they are tested.
- **It never touches an original.** The library is mounted read-only over CIFS with a read-only NAS
  credential. Not "configured not to write" — *unable* to write.
- **It is never exposed.** LAN only, no reverse proxy, no public port. The site fetches face crops
  server-side and caches them into its own derivative cache, so a browser never learns Immich exists.

---

## Prerequisites

- A LAN host with Docker + Docker Compose, disk for Immich's own preview store (it previews
  everything it indexes — budget on the same order as our thumb cache), and a route to the NAS.
- A **read-only** NAS account with access to the photo share. If one does not already exist, create
  it before going further; do not substitute a read-write account "just for setup".
- The site's `PhotosThumbCacheDir` reachable from whichever host will run `photos-sync-immich`,
  because that is where cached face crops are written.

---

## 1. Stage the compose file

Copy `scripts/photos-immich/` to the host. It contains:

| File | Purpose |
|---|---|
| `docker-compose.yml` | The four services, the read-only CIFS volume, and the pinned image tags |
| `.env.example` | Every credential and path, as placeholders |

```sh
cd photos-immich
cp .env.example .env
$EDITOR .env          # fill in the placeholders — see the table in step 2
mkdir -p data/upload data/postgres data/model-cache
```

`.env` holds real credentials. It is not committed, and it does not belong in any repository.

## 2. Fill in `.env`

| Key | What it is | Notes |
|---|---|---|
| `NAS_HOST` / `NAS_SHARE` | The photo share | |
| `NAS_READONLY_USER` / `NAS_READONLY_PASSWORD` | **Read-only** NAS credential | The whole safety story rests on this being read-only |
| `IMMICH_BIND_ADDRESS` | The host's LAN address | Never `0.0.0.0` on a machine with a public interface |
| `DB_USERNAME` / `DB_PASSWORD` / `DB_DATABASE_NAME` | Immich's own Postgres | Disposable data; the password still should not be `postgres` |
| `UPLOAD_LOCATION` / `DB_DATA_LOCATION` | Where Immich's derivatives and DB live | Local disk with room |

## 3. Bring it up, pinned

```sh
docker compose up -d
docker compose ps            # all four healthy
docker compose logs -f immich-server
```

The image tags in `docker-compose.yml` are **pinned on purpose** (`v1.120.2` at the time of writing).
Do not switch them to `:release`. Immich moves fast and has broken external-library flows before; an
unattended upgrade is precisely the failure §2.4 warns about, and our client will refuse to talk to a
major version it has not been tested against rather than mis-parse the API.

**Verify the mount is read-only before doing anything else:**

```sh
docker compose exec immich-server sh -c 'touch /usr/src/app/external/.write-test'
# Expected: "Read-only file system". If this SUCCEEDS, stop and fix the credential and the mount
# options before continuing — an Immich that can write to the collection is not a configuration
# problem, it is the one thing this whole vertical promises cannot happen.
```

## 4. Create the single user

Open `http://<IMMICH_BIND_ADDRESS>:2283` from inside the LAN. The first visit creates the admin
account.

**One user owns the library, and only one.** Immich runs its machine learning per user, so a second
account sharing the same library doubles the work for no benefit. There is no reason for a family
member to ever log in here — this is plumbing, and the family's surface is `/photos`.

## 5. Point an external library at the read-only mount

Administration → Libraries → **Add external library**, owned by that single user, with import path:

```
/usr/src/app/external
```

External libraries **index in place**: nothing is copied, moved or written. Scan it, then let it
finish — the first pass over a large collection takes a while.

Leave the library's scanning schedule at whatever suits the host. Nothing on our side depends on
Immich noticing a new file promptly; our own ingest is the discovery mechanism, and the sync simply
maps whatever Immich currently knows about.

## 6. Enable the ML jobs

Administration → Jobs. The ones this integration reads:

| Job | Why we want it |
|---|---|
| **Extract metadata** | EXIF, and the GPS that reverse geocoding needs |
| **Face detection** | Finds faces |
| **Facial recognition** | Groups them into clusters — the thing our tag queue consumes |
| **Smart search (CLIP)** | Embeddings; feeds the duplicate detection that catches crops and recolors a perceptual hash misses |
| **Duplicate detection** | The candidates that join our Near lane |

Thumbnail generation runs regardless and is what produces the per-cluster face crops the tag queue
shows.

CPU-only is fine to start. If a GPU is added later, schedule it deliberately rather than leaving it
always-on — that GPU is contended.

⚠ **Known upstream risk, eyes open:** face recognition on *external* libraries has a history of flaky
issues. That is not a blocker here, and it is a large part of why this feeds a suggestion queue
instead of writing tags: a wrong cluster costs one keystroke, not a corrupted family album.

## 7. Mint an API key

In Immich: account menu → **Account Settings → API Keys → New API Key**. Copy it once; it is not
shown again.

The key is only ever used server-side, from the host running the CLI. It is never sent to a browser
and never appears in any response our site produces.

## 8. Set the config keys on our side

Three keys, on the host that will run `photos-sync-immich` (the gateway-adjacent one), in
`appsettings.Development.json` locally or the production appsettings secret:

| Key | Value | Required |
|---|---|---|
| `ImmichBaseUrl` | `http://<IMMICH_BIND_ADDRESS>:2283` | yes |
| `ImmichApiKey` | the key from step 7 | yes |
| `ImmichLibraryId` | the external library's id, to restrict the sweep | optional — unset means "everything the key can see", which is correct for this single-library deployment |

With either of the first two unset, every surface degrades cleanly: the CLI says so and exits, the
site shows no sidecar affordances, and hand-tagging is unaffected. That is the normal state of every
host except this one.

## 9. Run the sync — dry first

From the host with those keys set:

```sh
# Reports what WOULD be written, and writes nothing. Always the first run against a real collection.
dotnet run --project src/MovieTheater/MovieTheater.csproj -- photos-sync-immich --dry-run --max-batches 2

# Then, for real, one lane at a time until each drains. Chunked and resumable like every pass here:
# each batch prints { processed, remaining, nextCursor } and --after resumes from it.
dotnet run --project src/MovieTheater/MovieTheater.csproj -- photos-sync-immich --pass assets
dotnet run --project src/MovieTheater/MovieTheater.csproj -- photos-sync-immich --pass people
dotnet run --project src/MovieTheater/MovieTheater.csproj -- photos-sync-immich --pass faces
dotnet run --project src/MovieTheater/MovieTheater.csproj -- photos-sync-immich --pass duplicates
```

The run prints the Immich version first and records it, so "which Immich produced these suggestions"
is answerable months later. An untested **major** version refuses the run outright.

Watch the `unmapped` and `ambiguous-path` counts on the assets lane. Mapping is a two-segment path
suffix match (folder + file name), and an ambiguous match is **skipped, never guessed** — mapping the
wrong photograph would attach a stranger's face suggestions to a family picture.

## 10. Name the clusters

Go to `/photos → People`. Imported clusters appear as **"unnamed group of N faces"** — deliberately
nameless, because names are the family's and live in our rows, not in a sidecar. Name one, or map it
onto a person who already exists.

Naming a cluster once retro-fits every suggestion it produced across the whole library. It is the
highest-leverage action in the feature, and it is one click.

Then work `/photos → Tag queue` from the keyboard: **Y** accepts, **N** refuses, **S** skips. A
refusal is remembered — the next sync will not propose that face on that photo again.

---

## Upgrading

1. Read Immich's release notes, specifically for external-library changes.
2. Bump the two image tags in `docker-compose.yml` by ONE version.
3. `docker compose pull && docker compose up -d`.
4. Re-run the read-only mount check from step 3. An upgrade that silently changed a mount would be
   the worst possible surprise.
5. `photos-sync-immich --dry-run --max-batches 2` and read the counts. A major-version bump will
   refuse; that refusal is the feature, and widening `ImmichClient.TestedMajor` is a deliberate code
   change made after checking the API, not a workaround.

## Throwing it away

```sh
docker compose down -v
rm -rf data/
```

Nothing of ours is lost: no person, no tag, no confirmed suggestion, no refusal, no album, no master
pick, no hand-set date. The `ImmichAssetId` / `ImmichPersonId` columns simply stop matching anything
and are re-derivable from paths if it ever comes back. Tagging continues by hand exactly as before —
which is the acceptance criterion this phase was built against, not an afterthought.
