using MovieTheater.Books.Db;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// The standalone site's <c>SeriesResolutionService.RebuildAsync</c> steps 1–4b, ported over the v2 inputs
    /// (ComicDetail.ParsedSeriesKey + SeriesKeyLink + Series overrides + CvVolume/ExternalWork names) as a PURE
    /// computation: canonical key per parsed key, the survivor per canonical group, its tiered name, the alias
    /// map. <see cref="Diff"/> compares that computation with what the migration copied from v1 — the verifier's
    /// proof that the port is faithful (a 0 diff), and later the input to the runtime rebuild job.
    /// </summary>
    public static class SeriesResolver
    {
        public sealed record SeriesRow(int Id, string ParsedKey, string CanonicalKey, string? Name, string? DisplayNameOverride);
        public sealed record Result(Dictionary<string, int> AliasMap, Dictionary<int, int> MergeMap, Dictionary<int, (string CanonicalKey, string Name, long? CvVolumeId, int? ExternalWorkId)> Survivors);

        /// <summary>Conservative normalization for the no-external-match bucket: lower, strip leading "the ", non-alphanumerics → spaces, collapse. No accent folding.</summary>
        public static string NormalizeKey(string parsedKey)
        {
            var s = parsedKey.Trim().ToLowerInvariant();
            if (s.StartsWith("the ", StringComparison.Ordinal)) s = s[4..];
            var cleaned = new string(s.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
            return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>Per-parsed-key provider ids to use instead of the link table (the verifier passes v1's own per-row majority).</summary>
        public sealed record Signal(IReadOnlyDictionary<string, long> CvByKey, IReadOnlyDictionary<string, int> ExtByKey);

        public static Result Compute(TargetWriter hot, Signal? signal = null)
        {
            // 1. per-parsed-key provider signal. v1 stamped ComicvineVolumeId/ExternalWorkId on every parsed-detail
            //    row from the FIRST link row carrying a key for that series name (any status; sticky once set) and
            //    then took the most populous id per key. In v2 the link table IS that signal, keyed by ParsedKey.
            var cvByKey = new Dictionary<string, long>(StringComparer.Ordinal);
            var extByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            if (signal != null)
            {
                foreach (var kv in signal.CvByKey) cvByKey[kv.Key] = kv.Value;
                foreach (var kv in signal.ExtByKey) extByKey[kv.Key] = kv.Value;
            }
            else
                foreach (var (rowid, payload) in hot.Pairs("SELECT rowid, ParsedKey || char(31) || Provider || char(31) || ProviderKey FROM SeriesKeyLink WHERE ProviderKey IS NOT NULL"))
                {
                    var p = payload!.Split(TargetWriter.Sep);
                    if (p[1] == ((int)Provider.Cv).ToString()) cvByKey.TryAdd(p[0], long.Parse(p[2]));
                    else if (p[1] == ((int)Provider.External).ToString()) extByKey.TryAdd(p[0], int.Parse(p[2]));
                }
            var countByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (n, key) in hot.Pairs("SELECT count(*), ParsedSeriesKey FROM ComicDetail WHERE ParsedSeriesKey IS NOT NULL AND ParsedSeriesKey <> '' GROUP BY ParsedSeriesKey"))
                countByKey[key!] = (int)n;

            // 2. display-name sources
            var volNames = hot.Pairs("SELECT Id, Name FROM CvVolume WHERE Name IS NOT NULL AND Name <> ''").ToDictionary(p => p.Item1, p => p.Item2!);
            var workTitles = hot.Pairs("SELECT Id, Title FROM ExternalWork WHERE Title IS NOT NULL AND Title <> ''").ToDictionary(p => (int)p.Item1, p => p.Item2!);

            // 3. series rows
            var series = hot.Pairs("SELECT Id, coalesce(ParsedKey,'') || char(31) || coalesce(CanonicalKey,'') || char(31) || coalesce(Name,'') || char(31) || coalesce(DisplayNameOverride,'') FROM Series")
                .Select(p => { var s = p.Item2!.Split(TargetWriter.Sep); return new SeriesRow((int)p.Item1, s[0], s[1], s[2].Length == 0 ? null : s[2], s[3].Length == 0 ? null : s[3]); }).ToList();

            string CanonicalKeyFor(string parsedKey) =>
                cvByKey.TryGetValue(parsedKey, out var cv) ? $"cv:{cv}" : extByKey.TryGetValue(parsedKey, out var ext) ? $"ext:{ext}" : $"parsed:{NormalizeKey(parsedKey)}";
            int CountFor(string parsedKey) => countByKey.GetValueOrDefault(parsedKey);

            // 4. survivors, names, alias + merge maps
            var aliasMap = new Dictionary<string, int>(StringComparer.Ordinal);
            var mergeMap = new Dictionary<int, int>();
            var survivors = new Dictionary<int, (string, string, long?, int?)>();
            foreach (var grp in series.GroupBy(s => CanonicalKeyFor(s.ParsedKey), StringComparer.Ordinal))
            {
                var key = grp.Key;
                var survivor = grp.OrderByDescending(s => s.CanonicalKey == key).ThenByDescending(s => CountFor(s.ParsedKey)).ThenBy(s => s.Id).First();
                long? canonVol = key.StartsWith("cv:", StringComparison.Ordinal) && long.TryParse(key.AsSpan(3), out var vid) ? vid : null;
                int? canonWork = key.StartsWith("ext:", StringComparison.Ordinal) && int.TryParse(key.AsSpan(4), out var wid) ? wid : null;
                var name =
                    !string.IsNullOrWhiteSpace(survivor.DisplayNameOverride) ? survivor.DisplayNameOverride.Trim() :
                    canonVol != null && volNames.TryGetValue(canonVol.Value, out var vn) && vn.Length > 0 ? vn :
                    canonWork != null && workTitles.TryGetValue(canonWork.Value, out var wt) && wt.Length > 0 ? wt :
                    survivor.ParsedKey;
                survivors[survivor.Id] = (key, name, canonVol, canonWork);
                foreach (var s in grp)
                {
                    aliasMap[s.ParsedKey] = survivor.Id;
                    if (s.Id != survivor.Id) mergeMap[s.Id] = survivor.Id;
                }
            }

            // 4b. parsed keys with no Series row of their own
            var survivorByCanonical = new Dictionary<string, int>(StringComparer.Ordinal);
            var survivorByNorm = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var s in series)
                if (aliasMap.TryGetValue(s.ParsedKey, out var sid))
                {
                    survivorByCanonical.TryAdd(CanonicalKeyFor(s.ParsedKey), sid);
                    survivorByNorm.TryAdd(NormalizeKey(s.ParsedKey), sid);
                }
            foreach (var key in countByKey.Keys)
            {
                if (aliasMap.ContainsKey(key)) continue;
                if (survivorByCanonical.TryGetValue(CanonicalKeyFor(key), out var sid) || survivorByNorm.TryGetValue(NormalizeKey(key), out sid))
                    aliasMap[key] = sid;
            }
            return new Result(aliasMap, mergeMap, survivors);
        }

        public sealed record DiffReport(int AliasAdded, int AliasChanged, int AliasRemoved, int SurvivorNameChanged, int SurvivorKeyChanged, int MergedAway, List<string> Samples)
        {
            public int Total => AliasAdded + AliasChanged + AliasRemoved + SurvivorNameChanged + SurvivorKeyChanged + MergedAway;
        }

        /// <summary>Recompute and compare with the stored Series/SeriesAlias rows; 0 = the port reproduces v1's derivation exactly.</summary>
        public static DiffReport Diff(TargetWriter hot, int sampleLimit = 20, Signal? signal = null)
        {
            var r = Compute(hot, signal);
            var storedAlias = hot.Pairs("SELECT SeriesId, ParsedKey FROM SeriesAlias").ToDictionary(p => p.Item2!, p => (int)p.Item1, StringComparer.Ordinal);
            var samples = new List<string>();
            int added = 0, changed = 0, removed = 0;
            foreach (var (key, sid) in r.AliasMap)
            {
                if (!storedAlias.TryGetValue(key, out var stored)) { added++; if (samples.Count < sampleLimit) samples.Add($"alias + '{key}' -> {sid}"); }
                else if (stored != sid) { changed++; if (samples.Count < sampleLimit) samples.Add($"alias ~ '{key}' {stored} -> {sid}"); }
            }
            foreach (var key in storedAlias.Keys) if (!r.AliasMap.ContainsKey(key)) { removed++; if (samples.Count < sampleLimit) samples.Add($"alias - '{key}'"); }
            int nameChanged = 0, keyChanged = 0;
            var stored2 = hot.Pairs("SELECT Id, coalesce(CanonicalKey,'') || char(31) || coalesce(Name,'') FROM Series").ToDictionary(p => (int)p.Item1, p => p.Item2!.Split(TargetWriter.Sep));
            foreach (var (id, (key, name, _, _)) in r.Survivors)
            {
                if (!stored2.TryGetValue(id, out var s)) continue;
                if (s[0] != key) { keyChanged++; if (samples.Count < sampleLimit) samples.Add($"series {id} key '{s[0]}' -> '{key}'"); }
                if (s[1] != name) { nameChanged++; if (samples.Count < sampleLimit) samples.Add($"series {id} name '{s[1]}' -> '{name}'"); }
            }
            return new DiffReport(added, changed, removed, nameChanged, keyChanged, r.MergeMap.Count, samples);
        }
    }
}
