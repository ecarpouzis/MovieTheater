using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace MovieTheater.Imdb
{
    /// <summary>Raised when IMDB serves its bot-challenge (HTTP 202 lite page) instead of
    /// the full title page, so the caller can re-warm the session and retry.</summary>
    public class ImdbChallengeException : Exception
    {
        public ImdbChallengeException(string message) : base(message) { }
    }

    /// <summary>
    /// Extracts normalized data from a single IMDB title page using a Playwright page.
    /// Reads the standardized JSON-LD block, the embedded __NEXT_DATA__ JSON, and the
    /// rendered cast list. See F:\Work\_scratch_imdb probes for the field shapes.
    /// </summary>
    public class ImdbTitleScraper
    {
        private static readonly Regex NmRegex = new Regex(@"nm\d+", RegexOptions.Compiled);

        private class CastDto
        {
            [JsonPropertyName("nm")] public string Nm { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("character")] public string Character { get; set; }
        }

        public async Task<ImdbScrapeResult> ScrapeAsync(IPage page, string imdbId, int castLimit, bool includePlotSummaries,
            ImdbPageCache cache = null)
        {
            var result = new ImdbScrapeResult { ImdbId = imdbId };

            var titleUrl = $"https://www.imdb.com/title/{imdbId}/";
            var response = await page.GotoAsync(
                titleUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });

            int status = response?.Status ?? 0;
            if (status == 404 || status == 410)
            {
                result.Found = false;
                result.FailureReason = $"IMDB returned HTTP {status} (title not found)";
                return result;
            }

            string nextDataRaw = await GetScriptTextAsync(page, "script#__NEXT_DATA__");
            if (string.IsNullOrWhiteSpace(nextDataRaw))
            {
                // The 202 lite/challenge page carries JSON-LD but no __NEXT_DATA__.
                throw new ImdbChallengeException($"No __NEXT_DATA__ for {imdbId} (HTTP {status}); session likely challenged.");
            }

            // Write-through cache: keep the full rendered page so future parser changes can re-derive
            // fields offline with zero IMDB traffic (§5.4). Only real (non-challenge) pages get here.
            if (cache != null)
                await cache.SaveAsync(imdbId, "title", await page.ContentAsync(), titleUrl, status == 0 ? 200 : status);

            using var nextDoc = JsonDocument.Parse(nextDataRaw);
            if (!TryPath(nextDoc.RootElement, out var atf, "props", "pageProps", "aboveTheFoldData")
                || atf.ValueKind != JsonValueKind.Object)
            {
                result.Found = false;
                result.FailureReason = "Title page had no aboveTheFoldData";
                return result;
            }

            result.Found = true;
            result.Title = GetString(atf, "titleText", "text");

            // IMDB titleType drives Movie.TitleType and tells us which titles are series (so we
            // additionally cache their episode pages). Shape: { id, text, isSeries, isEpisode, … }.
            if (TryPath(atf, out var titleType, "titleType") && titleType.ValueKind == JsonValueKind.Object)
            {
                result.TitleTypeId = GetString(titleType, "id");
                result.IsSeries = GetBool(titleType, "isSeries") ?? false;
                result.IsEpisode = GetBool(titleType, "isEpisode") ?? false;
            }

            result.Year = GetInt(atf, "releaseYear", "year");
            result.EndYear = GetInt(atf, "releaseYear", "endYear");
            var scrapedDate = ReadReleaseDate(atf);
            // IMDb's above-the-fold releaseDate can be a re-release/restoration date (e.g. a 2026
            // 4K re-run of a 1974 film), which would wrongly override the canonical year. The title's
            // authoritative year is releaseYear — if the scraped date's year disagrees, keep the
            // canonical year (Jan 1) rather than let the re-release win.
            if (scrapedDate.HasValue && result.Year.HasValue && scrapedDate.Value.Year != result.Year.Value)
                scrapedDate = new DateTime(result.Year.Value, 1, 1);
            result.ReleaseDate = scrapedDate ?? (result.Year.HasValue ? new DateTime(result.Year.Value, 1, 1) : (DateTime?)null);

            var seconds = GetInt(atf, "runtime", "seconds");
            if (seconds.HasValue && seconds.Value > 0)
                result.RuntimeMinutes = (int)Math.Round(seconds.Value / 60.0);

            result.MpaaRating = GetString(atf, "certificate", "rating");
            result.ImdbRating = GetDecimal(atf, "ratingsSummary", "aggregateRating");
            result.Plot = GetString(atf, "plot", "plotText", "plainText");

            if (TryPath(atf, out var genresArr, "genres", "genres") && genresArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in genresArr.EnumerateArray())
                {
                    var name = GetString(g, "text");
                    if (!string.IsNullOrWhiteSpace(name)) result.Genres.Add(name.Trim());
                }
            }

            // Directors/writers come from the standardized JSON-LD block (stable contract).
            string jsonLdRaw = await GetScriptTextAsync(page, "script[type='application/ld+json']");
            if (!string.IsNullOrWhiteSpace(jsonLdRaw))
            {
                using var ldDoc = JsonDocument.Parse(jsonLdRaw);
                var ld = ldDoc.RootElement;
                if (string.IsNullOrWhiteSpace(result.Title)) result.Title = GetString(ld, "name");
                if (result.MpaaRating == null) result.MpaaRating = GetString(ld, "contentRating");
                if (string.IsNullOrWhiteSpace(result.Plot)) result.Plot = GetString(ld, "description");
                if (result.Genres.Count == 0) AddLdGenres(ld, result.Genres);

                AddLdPeople(ld, "director", result.Directors);
                AddLdPeople(ld, "creator", result.Writers); // creator = writers (Persons only)
            }

            // Full billed cast (with characters) from the rendered DOM.
            await ReadCastAsync(page, result, castLimit);

            // Fallback: JSON-LD top-billed actors if the DOM cast was unavailable.
            if (result.Actors.Count == 0 && !string.IsNullOrWhiteSpace(jsonLdRaw))
            {
                using var ldDoc = JsonDocument.Parse(jsonLdRaw);
                AddLdPeople(ldDoc.RootElement, "actor", result.Actors);
            }

            if (includePlotSummaries)
                await ReadPlotSummariesAsync(page, imdbId, result, cache);

            // For series, cache the episode pages too — the raw data we'll later map into Episode
            // rows (docs/metadata-enrichment-plan.md §5.3). We only cache here; parsing/mapping into
            // the DB comes once the Episode schema exists.
            if (result.IsSeries && cache != null)
                await CacheSeriesEpisodesAsync(page, imdbId, cache);

            return result;
        }

        /// <summary>
        /// Caches the series' episodes pages so every episode is captured for later mapping. Loads the
        /// base /episodes/ page (carries the season list + first season), then each additional season
        /// it can find. Best-effort: failures here never abort the title.
        /// </summary>
        private async Task CacheSeriesEpisodesAsync(IPage page, string imdbId, ImdbPageCache cache)
        {
            try
            {
                var baseUrl = $"https://www.imdb.com/title/{imdbId}/episodes/";
                await page.GotoAsync(baseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
                var baseHtml = await page.ContentAsync();
                await cache.SaveAsync(imdbId, "episodes", baseHtml, baseUrl, 200);

                // Discover the season numbers from the season selector embedded in the page, then
                // cache each season's episode list. Regex over the rendered page is resilient to the
                // exact __NEXT_DATA__ nesting (which shifts between IMDB releases).
                var seasons = Regex.Matches(baseHtml, @"[?&]season=(\d{1,3})\b")
                    .Select(m => int.Parse(m.Groups[1].Value))
                    .Concat(Regex.Matches(baseHtml, @"""seasonNumber"":\s*(\d{1,3})\b").Select(m => int.Parse(m.Groups[1].Value)))
                    .Where(n => n >= 1 && n <= 100)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();

                foreach (var season in seasons)
                {
                    var pageType = $"episodes-s{season}";
                    if (cache.Has(imdbId, pageType)) continue; // already captured this run/earlier
                    var url = $"https://www.imdb.com/title/{imdbId}/episodes/?season={season}";
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
                    await cache.SaveAsync(imdbId, pageType, await page.ContentAsync(), url, 200);
                }
            }
            catch (PlaywrightException) { }
        }

        /// <summary>
        /// Loads /title/{id}/plotsummary/ and reads the long synopsis plus every
        /// contributed summary from its __NEXT_DATA__ contentData.categories. Non-fatal:
        /// failures here leave summaries empty rather than aborting the title.
        /// </summary>
        private async Task ReadPlotSummariesAsync(IPage page, string imdbId, ImdbScrapeResult result, ImdbPageCache cache)
        {
            try
            {
                var url = $"https://www.imdb.com/title/{imdbId}/plotsummary/";
                await page.GotoAsync(url,
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });

                if (cache != null)
                    await cache.SaveAsync(imdbId, "plotsummary", await page.ContentAsync(), url, 200);

                var raw = await GetScriptTextAsync(page, "script#__NEXT_DATA__");
                if (string.IsNullOrWhiteSpace(raw)) return;

                using var doc = JsonDocument.Parse(raw);
                if (!TryPath(doc.RootElement, out var categories, "props", "pageProps", "contentData", "categories")
                    || categories.ValueKind != JsonValueKind.Array)
                    return;

                foreach (var cat in categories.EnumerateArray())
                {
                    var catId = GetString(cat, "id");
                    if (!TryPath(cat, out var items, "section", "items") || items.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var item in items.EnumerateArray())
                    {
                        var text = CleanPlotText(GetString(item, "plotText"));
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        if (catId == "synopsis")
                        {
                            if (string.IsNullOrWhiteSpace(result.Synopsis)) result.Synopsis = text;
                        }
                        else // "summaries"
                        {
                            result.Summaries.Add(new ScrapedSummary { Author = GetString(item, "author"), Text = text });
                        }
                    }
                }
            }
            catch (PlaywrightException) { }
            catch (JsonException) { }
        }

        /// <summary>Strips IMDB markup (br tags, other tags) and decodes HTML entities.</summary>
        private static string CleanPlotText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var t = Regex.Replace(raw, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"<[^>]+>", "");
            t = System.Net.WebUtility.HtmlDecode(t);
            return t.Trim();
        }

        private static async Task ReadCastAsync(IPage page, ImdbScrapeResult result, int castLimit)
        {
            try
            {
                await page.WaitForSelectorAsync("[data-testid='title-cast-item']",
                    new PageWaitForSelectorOptions { Timeout = 12000, State = WaitForSelectorState.Attached });
            }
            catch (TimeoutException) { return; }
            catch (PlaywrightException) { return; }

            const string js = @"els => els.map(r => {
                const a = r.querySelector(""a[data-testid='title-cast-item__actor']"");
                const href = a ? a.getAttribute('href') : '';
                const m = href ? href.match(/nm\d+/) : null;
                const spans = Array.from(r.querySelectorAll(""[data-testid='cast-item-characters-link'] span""))
                    .map(s => s.textContent.trim()).filter(Boolean);
                return { nm: m ? m[0] : null, name: a ? a.textContent.trim() : null, character: spans.join(' / ') };
            })";

            var rows = await page.EvalOnSelectorAllAsync<CastDto[]>("[data-testid='title-cast-item']", js);
            if (rows == null) return;

            var seen = new HashSet<string>();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row?.Nm) || !seen.Add(row.Nm)) continue;
                result.Actors.Add(new ScrapedPerson
                {
                    ImdbNameId = row.Nm,
                    DisplayName = row.Name,
                    Character = string.IsNullOrWhiteSpace(row.Character) ? null : row.Character
                });
                if (result.Actors.Count >= castLimit) break;
            }
        }

        // ── JSON-LD helpers ───────────────────────────────────────────────

        private static void AddLdGenres(JsonElement ld, List<string> into)
        {
            if (!ld.TryGetProperty("genre", out var g)) return;
            if (g.ValueKind == JsonValueKind.String) into.Add(g.GetString().Trim());
            else if (g.ValueKind == JsonValueKind.Array)
                foreach (var item in g.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) into.Add(item.GetString().Trim());
        }

        private static void AddLdPeople(JsonElement ld, string prop, List<ScrapedPerson> into)
        {
            if (!ld.TryGetProperty(prop, out var arr)) return;

            var seen = new HashSet<string>(into.Where(p => p.ImdbNameId != null).Select(p => p.ImdbNameId));
            foreach (var item in EnumerateMaybeArray(arr))
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var type = GetString(item, "@type");
                if (type != null && type != "Person") continue; // skip Organizations in creator[]
                var url = GetString(item, "url");
                var name = GetString(item, "name");
                var nm = url != null ? NmRegex.Match(url).Value : null;
                if (string.IsNullOrEmpty(nm) || !seen.Add(nm)) continue;
                into.Add(new ScrapedPerson { ImdbNameId = nm, DisplayName = name });
            }
        }

        private static IEnumerable<JsonElement> EnumerateMaybeArray(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array)
                foreach (var i in e.EnumerateArray()) yield return i;
            else if (e.ValueKind == JsonValueKind.Object)
                yield return e;
        }

        private static DateTime? ReadReleaseDate(JsonElement atf)
        {
            if (!TryPath(atf, out var rd, "releaseDate") || rd.ValueKind != JsonValueKind.Object) return null;
            var y = GetInt(rd, "year"); var m = GetInt(rd, "month"); var d = GetInt(rd, "day");
            if (!y.HasValue) return null;
            try { return new DateTime(y.Value, m ?? 1, d ?? 1); }
            catch (ArgumentOutOfRangeException) { return new DateTime(y.Value, 1, 1); }
        }

        // ── __NEXT_DATA__ traversal helpers ───────────────────────────────

        private static async Task<string> GetScriptTextAsync(IPage page, string selector)
        {
            try
            {
                var loc = page.Locator(selector).First;
                if (await loc.CountAsync() == 0) return null;
                return await loc.TextContentAsync(new LocatorTextContentOptions { Timeout = 5000 });
            }
            catch (PlaywrightException) { return null; }
        }

        private static bool TryPath(JsonElement root, out JsonElement value, params string[] path)
        {
            value = root;
            foreach (var key in path)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value))
                {
                    value = default;
                    return false;
                }
            }
            return true;
        }

        private static string GetString(JsonElement root, params string[] path)
        {
            if (!TryPath(root, out var v, path)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static int? GetInt(JsonElement root, params string[] path)
        {
            if (!TryPath(root, out var v, path)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            return null;
        }

        private static decimal? GetDecimal(JsonElement root, params string[] path)
        {
            if (!TryPath(root, out var v, path)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
            return null;
        }

        private static bool? GetBool(JsonElement root, params string[] path)
        {
            if (!TryPath(root, out var v, path)) return null;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            return null;
        }
    }
}
