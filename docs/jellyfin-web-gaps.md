# Jellyfin-web player gaps — adoptable improvements

A comparison of Jellyfin's web player (`F:\Work\jellyfin-web-master`, the `jellyfin-web` source)
against our React player, to find plumbing worth adopting. Line numbers were valid at the time of
writing (jellyfin-web master @ 2026-06, our tree @ commit a099570-ish); re-verify before editing.

Our player is two shells (`Pages/Watch/VideoPlayer.js` on-demand, `Pages/Tv/TvPage.js` channels)
sharing hooks/engine (`streamEngine.js`, `useAdaptiveBitrate.js`, `streamAbr.js`, `subtitleStyle.js`,
`useWakeLock.js`). Server profile is built in `MovieTheater.Services/Jellyfin/JellyfinApi.cs`
(`BuildWebDeviceProfile`) from client `streamCapabilities.js`, consumed by `StreamController.cs`.

## Corrections / non-gaps (verified — do NOT chase)
- **Seeking is already at parity.** Single-file scrubs are a plain `video.currentTime =` set; we only
  rebuild the transcode on quality/audio/subtitle/ABR changes — same as Jellyfin's `changeStream`.
  The cold-start on a *far* seek is server-side ffmpeg relocation, identical in both clients.
- **We don't burn in ASS** — already delivered as VTT. ASS work is fidelity, not a re-encode fix.
- **dvdsub/VOBSUB micro-hitch is not fixable client-side** (Jellyfin burns those too); it's an ffmpeg
  overlay-timestamp issue. `maxBufferHole:0.5` remains the right client mitigation.
- **We're ahead** on: 120s/400MB spike buffer, `maxBufferHole:0.5`, manifest retry count, back-buffer
  memory cap, subtitle box-opacity + live preview + A/B drift sync, TV prewarm-next, volume persistence.

---

## Tier 1 — robustness & correctness (low effort/risk) — CORE DONE (uncommitted; needs live test)

| # | Gap | Our file | JF ref | Status |
|---|-----|----------|--------|--------|
| 1 | hls.js MEDIA_ERROR recovery never escalates (no `swapAudioCodec`, never gives up → infinite spinner). We copy video + transcode audio, so audio-decode mismatch is our likeliest fatal error and swapAudioCodec is the unused escape. | `streamEngine.js` | `htmlMediaHelper.js:79-110,318-322` | **DONE** — staged recover→swapAudioCodec→onFatal, per-instance counters |
| 2 | Fatal NETWORK_ERROR always `startLoad()`s unbounded → dead session = infinite reload, `onFatal` never fires. Classify response code (≥400/==0 → fatal; else bounded retry). | `streamEngine.js` | `htmlMediaHelper.js:272-339` | **DONE** — code≥400/==0/3-retry cap → onFatal |
| 3a | Dolby surround always re-encoded to AAC (HLS `AudioCodec` hard-wired). Add ac3/eac3 as HLS copy candidates (gated on client decode + channels). | `JellyfinApi.cs` | `browserDeviceProfile.js:585-607` | **DONE** — `AudioCodec = aac(+mp3)(+ac3)(+eac3)` |
| 3b | `maxAudioChannels` defaults to 2 → could floor surround browsers at 6. | `streamCapabilities.js` | `browserDeviceProfile.js:449-486` | **DEFERRED** — if AudioContext reports 2 the output path is genuinely stereo, so flooring gains nothing audible and would make the "Playing: 5.1" readout lie. Not a clear win. |
| 4 | 10-bit H.264 (Hi10P anime) copied → fails (no `VideoProfile` guard). | `JellyfinApi.cs` | `browserDeviceProfile.js:1129-1148,1277-1283` | **DONE** — `VideoProfile EqualsAny high\|main\|baseline\|constrained baseline` |
| 5 | Watch loads from 0 then seeks instead of passing `startPosition` (TV already does it right). | `VideoPlayer.js` | `plugin.js:455-456` | **DONE** — hls.js opens at `startPosition`; native branches still post-load seek |
| 6 | `maxMaxBufferLength` unset → forward buffer can balloon to 600s default at low bitrate. | `streamEngine.js` | `plugin.js:458-459` | **DONE** — set 300 |

Device-profile correctness guards — **DONE** (new caps threaded streamCapabilities.js → StreamController
StartRequest → ClientCapabilities → BuildWebDeviceProfile):
- **HE-AAC**: probe `mp4a.40.5` → `caps.HeAac`; when false, a `VideoAudio/aac` CodecProfile `NotEquals
  AudioProfile HE-AAC` forces HE-AAC tracks to transcode to LC-AAC. (Usually inert — HE-AAC is near-universal.)
- **HEVC Main-10**: probe `hvc1.2.4.L153` → `caps.HevcMain10`; HEVC CodecProfile now carries
  `VideoProfile EqualsAny main|main 10` (capable) or `main` (8-bit only). Safe because the copy path only
  activates when `caps.Hevc` is already true and isTypeSupported is a reliable oracle.
- **AV1**: added 10-bit probe `av01.0.15M.10` → `caps.Av110Bit`; AV1 CodecProfile gates copy to
  `VideoProfile main`, `VideoBitDepth <= 8` unless 10-bit, and SDR (+HDR10/HLG only when HDR display +
  10-bit). (AV1 is ~absent from the library, so low impact but no longer a latent broken-copy.)
- **Dolby Vision**: probe `dvh1.05.06`/`dvh1.08.06`/`dvhe.*` → `caps.DolbyVision`; DOVI ranges are now
  appended to the HEVC copy-allowed set ONLY when DV is decodable (≈Safari). Non-DV HDR displays
  (Chrome/Edge) no longer copy a DOVI source (which rendered broken) — it tonemaps/transcodes instead.

Build status: UI lint/build/test green; C# full project builds 0 errors. NOT committed.

**Verification status:** UI lint/build/test green; C# `MovieTheater.Services` builds 0 errors. NOT committed.
Picks up after: UI rebuild/reload (client items 1,2,5,6) + API restart (server items 3a,4 — profile is
built at PlaybackInfo time, so a fresh stream must be started to exercise it).

## Tier 2 — UX/features — **DONE** (uncommitted; build/lint/test green)
Three new shared hooks, wired into both players (Watch `VideoPlayer.js` + TV `TvPage.js`):
- **`useMediaSession.js`** — metadata + transport handlers + **`setPositionState`** (the missing piece that
  froze the lock-screen scrubber on Chrome/Android). Watch wires play/pause/seek; TV wires
  play/pause→shared channel pause, prev/next→channel down/up, no seek. TV previously had no media session.
- **`usePictureInPicture.js`** — `{supported, active, toggle}` (standard API + Safari presentation-mode
  fallback); PiP button added to both control bars (+ glyph CSS in both players' CSS).
- **`usePlaybackRate.js`** — persisted speed (0.5–2×), **Watch only** (a per-viewer rate would desync the
  TV schedule clock). Menu "Speed" section + `<` / `>` keys; re-applies after our per-source element teardown.

## Tier 3 — subtitles & bandwidth
- **PGS client rendering via `libpgs`** — **DONE** (commit 6e00f7f, needs browser test). Removes the
  re-encode when a Blu-ray PGS sub is on: server advertises `pgssub` External + tags each track `kind`
  (text/image-pgs/image-burn) + never burns PGS; both players draw it via `usePgsSubtitle.js` (libpgs
  canvas overlay; self-contained wasm-free worker bundled via Vite `?url`). libpgs is wasm-free so it
  bundled cleanly. Verify: PGS rendering, canvas overlay alignment, timing.
- **ASS/SSA fidelity via `@jellyfin/libass-wasm`** — **DONE** (commit 4a10e31, needs browser test).
  Server advertises ass/ssa External; StreamController `IsAssSubtitle` tags kind="ass"; both players render
  via `useAssSubtitle.js` (SubtitlesOctopus). The 2.3 MB wasm + worker + legacy + fallback font are staged
  to `public/libass/` by `scripts/copy-libass.mjs` (prestart/prebuild hooks; gitignored; plain-Node copy to
  dodge a rolldown-vite plugin; Docker `npm run build` fires prebuild too). ASS excluded from `<track>` and
  the VTT-only style/delay UI. FOLLOW-UPS: embedded fonts (server MediaAttachments → libass `fonts`) so
  signs use the right face (fallback font until then); routing the delay UI to libass `timeOffset` for ASS.
- **Custom `<div>` subtitle renderer** (`plugin.js:1366-1403`) — beats `::cue` on iOS/macOS (system caption
  override) and unlocks secondary subtitles; an enhancement (current `::cue` works), needs browser test.
- **Bandwidth probe for the opener** — TENSION with the deliberate "Auto opens at Original, drops on stall"
  choice. Only sane as "open Original UNLESS the link is provably weak," and the full version needs a new
  gateway bitrate-test endpoint. Deferred pending a design call.
