using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace MovieTheater.RottenTomatoes
{
    /// <summary>Raised when RT serves a bot challenge instead of real search results,
    /// so the caller can re-warm the session and retry (mirrors the IMDB scraper).</summary>
    public class RtChallengeException : Exception
    {
        public RtChallengeException(string message) : base(message) { }
    }

    /// <summary>
    /// Resolves a movie on rottentomatoes.com via RT's own search page and reads both the
    /// Tomatometer (critics) and Popcornmeter (audience) scores off the resolved movie page.
    /// Uses a Playwright page exactly like <see cref="Imdb.ImdbTitleScraper"/>; no Google.
    ///
    /// Search rows are <c>&lt;search-page-media-row release-year tomatometer-score&gt;</c> custom
    /// elements carrying a child <c>&lt;a data-qa="info-name" href=".../m/slug"&gt;Title&lt;/a&gt;</c>.
    /// Both scores render server-side into <c>&lt;rt-text slot="critics-score|audience-score"&gt;83%&lt;/rt-text&gt;</c>.
    /// </summary>
    public class RtScraper
    {
        private static readonly Regex DigitsRegex = new Regex(@"\d+", RegexOptions.Compiled);

        // Max year gap we'll tolerate between our movie and an exact-title RT match before
        // treating it as the wrong film (e.g. a remake, or a TV version not on /m/).
        private const int YearTolerance = 3;

        // Both normalized titles must be at least this long to match by substring (Tier 2),
        // so short titles ("It", "Up", "Her") don't false-match longer ones.
        private const int MinContainsLen = 5;

        private class RowDto
        {
            [JsonPropertyName("url")] public string Url { get; set; }
            [JsonPropertyName("title")] public string Title { get; set; }
            [JsonPropertyName("year")] public string Year { get; set; }
        }

        /// <summary>Resolve the best-matching RT movie page for our title/year, then read both scores.</summary>
        public async Task<RtScoreResult> ScrapeAsync(IPage page, string title, int? year)
        {
            var result = new RtScoreResult { SearchTitle = title };

            var rows = await SearchAsync(page, title);
            var match = PickBestMatch(rows, title, year);
            if (match == null)
            {
                result.Found = false;
                result.FailureReason = rows.Count == 0
                    ? "No RT movie results for the title."
                    : $"No RT result confidently matched '{title}'"
                        + (year.HasValue ? $" ({year})." : ".");
                return result;
            }

            result.Found = true;
            result.ResolvedUrl = match.Url;
            result.MatchedTitle = match.Title;
            result.MatchedYear = ParseYear(match.Year);

            await ReadScoresAsync(page, match.Url, result);
            return result;
        }

        /// <summary>Loads the search page and returns every movie (/m/) result row.</summary>
        private async Task<List<RowDto>> SearchAsync(IPage page, string title)
        {
            var url = "https://www.rottentomatoes.com/search?search=" + Uri.EscapeDataString(title);
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });

            try
            {
                await page.WaitForSelectorAsync("search-page-media-row",
                    new PageWaitForSelectorOptions { Timeout = 12000, State = WaitForSelectorState.Attached });
            }
            catch (TimeoutException)
            {
                // No rows can mean a genuinely empty search or a bot challenge; distinguish
                // by whether the page rendered the search shell at all.
                var hasShell = await page.Locator("search-page-result, [data-qa='search-result']").CountAsync() > 0;
                if (!hasShell)
                    throw new RtChallengeException("RT search returned no result shell; session likely challenged.");
                return new List<RowDto>();
            }
            catch (PlaywrightException)
            {
                return new List<RowDto>();
            }

            const string js = @"els => els.map(r => {
                const a = r.querySelector(""a[data-qa='info-name']"") || r.querySelector(""a[slot='title']"");
                return {
                    url: a ? a.getAttribute('href') : null,
                    title: a ? a.textContent.trim() : null,
                    year: r.getAttribute('release-year') || r.getAttribute('start-year') || ''
                };
            })";

            var rows = await page.EvalOnSelectorAllAsync<RowDto[]>("search-page-media-row", js);
            return (rows ?? Array.Empty<RowDto>())
                .Where(r => !string.IsNullOrWhiteSpace(r.Url) && r.Url.Contains("/m/"))
                .ToList();
        }

        /// <summary>Navigates to the resolved movie page and reads both score slots.</summary>
        private async Task ReadScoresAsync(IPage page, string movieUrl, RtScoreResult result)
        {
            await page.GotoAsync(movieUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            result.Tomatometer = await ReadScoreAsync(page, "rt-text[slot='critics-score']");
            result.Popcornmeter = await ReadScoreAsync(page, "rt-text[slot='audience-score']");
        }

        private static async Task<int?> ReadScoreAsync(IPage page, string selector)
        {
            try
            {
                var loc = page.Locator(selector).First;
                if (await loc.CountAsync() == 0) return null;
                var text = await loc.TextContentAsync(new LocatorTextContentOptions { Timeout = 5000 });
                return ParsePercent(text);
            }
            catch (PlaywrightException) { return null; }
        }

        // RT shows "83%" when scored and "- -" (no digits) when not; treat the latter as null.
        private static int? ParsePercent(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = DigitsRegex.Match(text);
            if (!m.Success) return null;
            return int.TryParse(m.Value, out var v) && v >= 0 && v <= 100 ? v : (int?)null;
        }

        private static int? ParseYear(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var m = DigitsRegex.Match(raw);
            return m.Success && int.TryParse(m.Value, out var v) ? v : (int?)null;
        }

        // Prefer an exact normalized title match; among those (or all rows if none match by
        // title) pick the one closest in release year. Returns null when nothing is a
        // plausible match — caller flags the row for review rather than storing junk scores.
        private static RowDto PickBestMatch(List<RowDto> rows, string title, int? year)
        {
            if (rows.Count == 0) return null;

            var want = Normalize(title);
            // Tier 1: exact normalized title.
            var exact = rows.Where(r => Normalize(r.Title) == want).ToList();
            if (exact.Count > 0)
                return WithinYear(ClosestByYear(exact, year), year);

            // Tier 2: an RT title that *extends* ours with a subtitle — recovers shortened
            // bases (e.g. "Anchorman" → "Anchorman: The Legend of Ron Burgundy", "Ocean's"
            // → "Ocean's Eleven"). Deliberately one-directional (theirs contains ours, not the
            // reverse) so a numbered entry like "pinkpanther02" can't grab the base "pinkpanther"
            // and mis-score a different franchise film. Min length guards short titles ("It"),
            // and the year guard rejects wrong-era matches.
            if (want.Length >= MinContainsLen)
            {
                var contains = rows.Where(r => Normalize(r.Title).Contains(want)).ToList();
                if (contains.Count > 0)
                    return WithinYear(ClosestByYear(contains, year), year);
            }

            // Tier 3: no title hit, but exactly one result shares our year.
            if (year.HasValue)
            {
                var yearMatches = rows.Where(r => ParseYear(r.Year) == year.Value).ToList();
                if (yearMatches.Count == 1) return yearMatches[0];
            }
            return null;
        }

        // Reject a match whose year is far from ours — almost certainly a different film
        // (remake) or the wrong medium, so we don't attribute its scores to our movie.
        private static RowDto WithinYear(RowDto row, int? year)
        {
            if (row == null) return null;
            if (year.HasValue && ParseYear(row.Year) is int by && Math.Abs(by - year.Value) > YearTolerance)
                return null;
            return row;
        }

        private static RowDto ClosestByYear(List<RowDto> rows, int? year)
        {
            if (!year.HasValue || rows.Count == 1) return rows[0];
            return rows
                .OrderBy(r => ParseYear(r.Year) is int y ? Math.Abs(y - year.Value) : int.MaxValue)
                .First();
        }

        // Mirror of the IMDB title normalization (ImdbDataApplier.Normalize): article- and
        // punctuation-insensitive, so our "Matrix, The" matches RT's "The Matrix".
        private static string Normalize(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            var t = title.ToLowerInvariant();
            t = Regex.Replace(t, @"\(\s*\d{4}.*?\)", " ");   // drop trailing (year)
            t = Regex.Replace(t, @",\s*the\b", " ");          // "matrix, the" -> "matrix"
            t = Regex.Replace(t, @"^\s*the\b", " ");          // "the matrix" -> "matrix"
            t = Regex.Replace(t, @"[^a-z0-9]", "");           // strip punctuation/space
            return t;
        }
    }
}
