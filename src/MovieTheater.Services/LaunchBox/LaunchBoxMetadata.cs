using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace MovieTheater.Services.LaunchBox
{
    /// <summary>
    /// The LaunchBox Games Database metadata dump — our PRIMARY source of game review scores.
    /// <para>
    /// This is NOT a scrape: LaunchBox publishes a single public <c>Metadata.zip</c> (~105 MB, 183k games,
    /// 119k of them rated) with no API key, no rate limit and no bot challenge. We download it once, stream
    /// it, and keep only (platform, normalized-title) → (stars, votes).
    /// </para>
    /// Why it displaced IGDB for ratings: it rates ~83% of our cards vs IGDB's 34%, and IGDB's *user* score
    /// (the only component most retro titles have — they carry no critic aggregate) is badly skewed on
    /// obscure games. Canonical example: American Chopper scores 99.5/100 on IGDB from 49 user votes, while
    /// LaunchBox puts it at 65.7 — and its sequel sits at 34.5 on IGDB. IGDB is now a fallback only.
    /// <para>
    /// Ratings are 0–5 stars in the dump; <see cref="Entry.Score100"/> rescales to our 0–100 convention.
    /// </para>
    /// </summary>
    public sealed class LaunchBoxMetadata
    {
        public const string DumpUrl = "https://gamesdb.launchbox-app.com/Metadata.zip";

        public readonly record struct Entry(double Stars, int Votes, string? Genres, string? Overview,
                                            string? Developer, string? Publisher)
        {
            /// <summary>0–5 stars → our 0–100 scale, matching IGDB's range so the two are comparable.</summary>
            public double Score100 => Stars / 5.0 * 100.0;
        }

        /// <summary>LaunchBox platform name → our <c>ArcadeGame.System</c> code. Platforms we don't carry are
        /// skipped, which is what keeps the in-memory index small (~33k of 183k games).</summary>
        public static readonly IReadOnlyDictionary<string, string> PlatformToSystem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Super Nintendo Entertainment System"] = "snes",
            ["Nintendo Entertainment System"] = "nes",
            ["Sony Playstation 2"] = "ps2",
            ["Sony Playstation"] = "ps1",
            ["Sega Genesis"] = "genesis",
            ["Nintendo 64"] = "n64",
            ["Nintendo Game Boy Advance"] = "gba",
            ["Arcade"] = "arcade",
            ["Nintendo GameCube"] = "gc",
            ["Nintendo Game Boy"] = "gb",
            ["Nintendo Game Boy Color"] = "gbc",
            ["Sega Dreamcast"] = "dc",
            ["Sony PSP"] = "psp",
            ["Atari 2600"] = "a2600",
            ["Sega Master System"] = "sms",
            ["Sega Game Gear"] = "gg",
            ["NEC TurboGrafx-16"] = "pce",
            ["Atari 7800"] = "a7800",
            ["Atari Lynx"] = "lynx",
            ["Nintendo Virtual Boy"] = "vb",
            ["Sega 32X"] = "sega32x",
            ["Sega CD"] = "segacd",
            ["SNK Neo Geo Pocket Color"] = "ngpc",
            ["WonderSwan Color"] = "wsc",
            ["Nintendo Famicom Disk System"] = "fds",
            ["SNK Neo Geo AES"] = "neogeo",
            ["Sega SG-1000"] = "sg1000",
        };

        private static readonly Regex Parenthetical = new(@"\(.*?\)", RegexOptions.Compiled);
        private static readonly Regex NonAlnum = new(@"[^a-z0-9]+", RegexOptions.Compiled);
        private static readonly HashSet<string> Articles = new(StringComparer.Ordinal) { "the", "a", "an" };

        /// <summary>
        /// Collapse a title to a comparison key. The load-bearing detail is that ARTICLES ARE DROPPED
        /// POSITIONALLY, not just un-inverted from the end: No-Intro writes "Legend of Zelda, The - Link's
        /// Awakening DX" — the article sits mid-title, before the subtitle — while LaunchBox writes "The
        /// Legend of Zelda: Link's Awakening DX". Stripping only a trailing ", The" misses those; dropping
        /// "the/a/an" as tokens matched 555 additional cards (79.4% → 82.6%).
        /// Also folds "&"→"and", drops (USA)/(Rev 1) parentheticals, and treats -, : and / as separators.
        /// </summary>
        public static string NormalizeTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            var s = title.ToLowerInvariant().Replace("&", "and");
            s = Parenthetical.Replace(s, " ");
            s = NonAlnum.Replace(s, " ");
            var sb = new StringBuilder(s.Length);
            foreach (var tok in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!Articles.Contains(tok)) sb.Append(tok);
            return sb.ToString();
        }

        /// <summary>
        /// Every key a card's title may be looked up under: the title itself, plus each side of a "/" or "~"
        /// split. Our romset-derived titles carry dual names ("Red Earth / War-Zard", "Dodge 'Em ~ Dodger Cars")
        /// where LaunchBox indexes only one of them. First hit wins, so the full title is tried first.
        /// </summary>
        public static IEnumerable<string> TitleKeys(string title)
        {
            var whole = NormalizeTitle(title);
            if (whole.Length > 0) yield return whole;
            if (title is null) yield break;

            foreach (var part in title.Split('/', '~', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var k = NormalizeTitle(part);
                if (k.Length > 0 && k != whole) yield return k;
            }
        }

        /// <summary>Ensure the dump exists on disk, downloading it if absent (or if <paramref name="refresh"/>).
        /// Downloads to a .part file and moves on success, so an interrupted run can't leave a truncated zip
        /// that later parses as "no games found".</summary>
        public static async Task<string> EnsureDumpAsync(HttpClient http, string cachePath, bool refresh,
                                                         Action<string>? log = null, CancellationToken ct = default)
        {
            var full = Path.GetFullPath(cachePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (File.Exists(full) && !refresh)
            {
                log?.Invoke($"Using cached dump: {full} ({new FileInfo(full).Length / 1_000_000} MB)");
                return full;
            }

            var tmp = full + ".part";
            log?.Invoke($"Downloading {DumpUrl} …");
            using (var resp = await http.GetAsync(DumpUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst, ct);
            }
            if (File.Exists(full)) File.Delete(full);
            File.Move(tmp, full);
            log?.Invoke($"Downloaded {new FileInfo(full).Length / 1_000_000} MB → {full}");
            return full;
        }

        /// <summary>
        /// Stream Metadata.xml into an index keyed by (system, normalized title). Only rated entries are kept.
        /// When several LaunchBox rows collapse to the same key, the one with the MOST votes wins.
        ///
        /// <para>The index carries each game's PRIMARY name plus its <c>&lt;GameAlternateName&gt;</c> aliases
        /// (68k of them). Aliases are what map our romset/No-Intro Japanese titles onto the Western releases
        /// LaunchBox rates — "Starwing"→Star Fox, "Ryuuko no Ken"→Art of Fighting, "Baku Bomber Man 2"→
        /// Bomberman 64. They took coverage from 84.8% to 96.7%.</para>
        ///
        /// <para>Two safety rules, because raw aliases are dirty:
        /// (1) a game's own primary name ALWAYS beats someone else's alias — 250 aliases collide with a
        /// different game's real name (<c>elitserien95</c> is both an alias of NHL 95 and the actual name of
        /// Elitserien 95); (2) an alias claimed by more than one game is dropped, as are junk aliases shorter
        /// than 4 chars or all-digits (LaunchBox really does store aliases like "64", "3" and "x").</para>
        ///
        /// <para>Streamed with XmlReader (the file is ~500 MB uncompressed — never load it as a document).</para>
        /// </summary>
        public static Dictionary<(string System, string Key), Entry> BuildIndex(string zipPath, Action<string>? log = null)
        {
            var index = new Dictionary<(string, string), Entry>();
            // dbId → the rated game. Alias rows carry only a DatabaseID (no platform), so they're collected
            // during the single pass and resolved against this afterwards.
            var byId = new Dictionary<string, (string Sys, Entry Entry)>();
            var aliasRows = new List<(string DbId, string Key)>();

            using var zip = ZipFile.OpenRead(zipPath);
            var meta = zip.GetEntry("Metadata.xml")
                ?? throw new InvalidOperationException("Metadata.xml not found in the LaunchBox dump.");

            using var stream = meta.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });

            int games = 0, rated = 0;
            reader.MoveToContent();
            // NOTE the loop shape. Both XNode.ReadFrom() and ReadElementContentAsString() leave the reader
            // ALREADY positioned on the next node, so pairing either with a `while (reader.Read())` header
            // silently skips every other sibling. Hand-walking the children that way dropped 2/3 of the
            // ratings; adding a Read() around ReadFrom() then skipped every other <Game> (91,882 of 183,763).
            // So: only advance when we did NOT consume a node.
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }

                if (reader.Name == "GameAlternateName")
                {
                    var alt = (XElement)XNode.ReadFrom(reader);
                    var altId = alt.Element("DatabaseID")?.Value;
                    var ak = NormalizeTitle(alt.Element("AlternateName")?.Value);
                    // Junk aliases: LaunchBox really does store "64", "3" and "x" as alternate names.
                    if (altId != null && ak.Length >= 4 && !ak.All(char.IsDigit))
                        aliasRows.Add((altId, ak));
                    continue;
                }

                if (reader.Name != "Game") { reader.Read(); continue; }
                games++;

                var el = (XElement)XNode.ReadFrom(reader);   // consumes </Game>, lands on the next node

                string? dbId = el.Element("DatabaseID")?.Value;
                string? platform = el.Element("Platform")?.Value;
                string? name = el.Element("Name")?.Value;
                string? genres = el.Element("Genres")?.Value;
                string? overview = el.Element("Overview")?.Value;
                string? dev = el.Element("Developer")?.Value;
                string? pub = el.Element("Publisher")?.Value;

                double.TryParse(el.Element("CommunityRating")?.Value, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var stars);
                int.TryParse(el.Element("CommunityRatingCount")?.Value, NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out var votes);

                if (platform == null || name == null) continue;
                if (!PlatformToSystem.TryGetValue(platform, out var sys)) continue;
                if (stars <= 0 || votes <= 0) continue;   // unrated rows carry no signal

                var key = NormalizeTitle(name);
                if (key.Length == 0) continue;
                rated++;

                var entry = new Entry(stars, votes, Trim(genres, 200), Trim(overview, 1000), Trim(dev, 200), Trim(pub, 200));
                if (!index.TryGetValue((sys, key), out var prior) || votes > prior.Votes)
                    index[(sys, key)] = entry;
                if (dbId != null) byId[dbId] = (sys, entry);
            }
            int primaries = index.Count;

            // Alias pass. Resolve each alias to its game's system, then drop the unsafe ones:
            //  · already a primary name → the real game keeps it (250 aliases collide with another game's name)
            //  · claimed by more than one game → ambiguous, drop
            var claims = new Dictionary<(string, string), HashSet<string>>();
            foreach (var (dbId, ak) in aliasRows)
            {
                if (!byId.TryGetValue(dbId, out var g)) continue;      // alias of an unrated / off-platform game
                var k = (g.Sys, ak);
                if (index.ContainsKey(k)) continue;                    // a real game owns this name
                if (!claims.TryGetValue(k, out var set)) claims[k] = set = new HashSet<string>();
                set.Add(dbId);
            }
            int aliases = 0, ambiguous = 0;
            foreach (var (k, dbIds) in claims)
            {
                if (dbIds.Count != 1) { ambiguous++; continue; }
                index[k] = byId[dbIds.First()].Entry;
                aliases++;
            }

            log?.Invoke($"Parsed {games:N0} games; indexed {primaries:N0} rated titles + {aliases:N0} safe aliases "
                      + $"= {index.Count:N0} keys ({ambiguous:N0} ambiguous aliases dropped, from {rated:N0} rated rows).");
            return index;
        }

        /// <summary>A game's real simultaneous-player count, from the dump's &lt;MaxPlayers&gt;.</summary>
        public readonly record struct Seats(int MaxPlayers, bool Cooperative, int Votes);

        /// <summary>
        /// Stream Metadata.xml into an index of (system, normalized title) → per-game player count.
        ///
        /// <para>Why this exists: <c>ArcadeGame.MaxPlayers</c> is set at ingest to a per-SYSTEM blanket (the
        /// core's controller-port ceiling — PS2 2, N64 4, SNES 5), which over-states almost every game. Shadow
        /// of the Colossus advertised "2P" purely because it is a PS2 game. LaunchBox publishes a real
        /// per-game <c>&lt;MaxPlayers&gt;</c> for ~77% of our cards; the rest keep the blanket.</para>
        ///
        /// <para>Deliberately NOT gated on ratings, unlike <see cref="BuildIndex"/>: a game with no community
        /// votes still has a trustworthy player count, and dropping unrated rows would throw away thousands of
        /// seat facts. Same alias rules, same most-votes-wins tie-break.</para>
        ///
        /// <para>The one signal we do NOT use here is IGDB's <c>game_modes</c>. It has false negatives that
        /// would be catastrophic: GoldenEye 007 on N64 — a four-player split-screen landmark — is recorded as
        /// "Single player". LaunchBox has no MaxPlayers for GoldenEye at all, so it correctly keeps the N64
        /// blanket of 4. Absent data must mean "leave it alone", never "it's single player".</para>
        /// </summary>
        public static Dictionary<(string System, string Key), Seats> BuildSeatIndex(string zipPath, Action<string>? log = null)
        {
            var index = new Dictionary<(string, string), Seats>();
            var byId = new Dictionary<string, (string Sys, Seats Seats)>();
            var aliasRows = new List<(string DbId, string Key)>();

            using var zip = ZipFile.OpenRead(zipPath);
            var meta = zip.GetEntry("Metadata.xml")
                ?? throw new InvalidOperationException("Metadata.xml not found in the LaunchBox dump.");

            using var stream = meta.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });

            int games = 0, withSeats = 0;
            reader.MoveToContent();
            // Same loop shape as BuildIndex — see the note there. Only advance when we did NOT consume a node.
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }

                if (reader.Name == "GameAlternateName")
                {
                    var alt = (XElement)XNode.ReadFrom(reader);
                    var altId = alt.Element("DatabaseID")?.Value;
                    var ak = NormalizeTitle(alt.Element("AlternateName")?.Value);
                    if (altId != null && ak.Length >= 4 && !ak.All(char.IsDigit))
                        aliasRows.Add((altId, ak));
                    continue;
                }

                if (reader.Name != "Game") { reader.Read(); continue; }
                games++;

                var el = (XElement)XNode.ReadFrom(reader);

                string? platform = el.Element("Platform")?.Value;
                string? name = el.Element("Name")?.Value;
                if (platform == null || name == null) continue;
                if (!PlatformToSystem.TryGetValue(platform, out var sys)) continue;

                if (!int.TryParse(el.Element("MaxPlayers")?.Value, NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out var maxPlayers) || maxPlayers <= 0)
                    continue;   // no seat fact → this game contributes nothing; the blanket stands
                bool coop = string.Equals(el.Element("Cooperative")?.Value, "true", StringComparison.OrdinalIgnoreCase);
                int.TryParse(el.Element("CommunityRatingCount")?.Value, NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out var votes);

                var key = NormalizeTitle(name);
                if (key.Length == 0) continue;
                withSeats++;

                var entry = new Seats(maxPlayers, coop, votes);
                if (!index.TryGetValue((sys, key), out var prior) || votes > prior.Votes)
                    index[(sys, key)] = entry;
                var dbId = el.Element("DatabaseID")?.Value;
                if (dbId != null) byId[dbId] = (sys, entry);
            }
            int primaries = index.Count;

            // Alias pass, same two safety rules as BuildIndex: a real name always beats someone else's alias,
            // and an alias claimed by more than one game is ambiguous and dropped.
            var claims = new Dictionary<(string, string), HashSet<string>>();
            foreach (var (dbId, ak) in aliasRows)
            {
                if (!byId.TryGetValue(dbId, out var g)) continue;
                var k = (g.Sys, ak);
                if (index.ContainsKey(k)) continue;
                if (!claims.TryGetValue(k, out var set)) claims[k] = set = new HashSet<string>();
                set.Add(dbId);
            }
            int aliases = 0, ambiguous = 0;
            foreach (var (k, dbIds) in claims)
            {
                if (dbIds.Count != 1) { ambiguous++; continue; }
                index[k] = byId[dbIds.First()].Seats;
                aliases++;
            }

            log?.Invoke($"Parsed {games:N0} games; indexed {primaries:N0} titles with a player count + {aliases:N0} safe aliases "
                      + $"= {index.Count:N0} keys ({ambiguous:N0} ambiguous aliases dropped, from {withSeats:N0} rows).");
            return index;
        }

        private static string? Trim(string? s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            return s.Length <= max ? s : s[..max];
        }
    }
}
