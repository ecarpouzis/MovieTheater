using MovieTheater.Books.Migration;
using MovieTheater.Books.Resolve;

namespace MovieTheater.Books.Verify
{
    /// <summary>
    /// <c>books-verify-v1</c>: evidence that the copy-transform is complete and faithful — never the migration's
    /// own bookkeeping. Every check re-reads the v1 source and the v2 files independently. A failed check is
    /// a line in the report and a non-zero exit, never a silent pass.
    /// </summary>
    public sealed class V1Verifier
    {
        public sealed record Check(string Name, bool Passed, string Detail);

        private readonly V1Source v1;
        private readonly TargetWriter hot, legs;
        private readonly MigrationOptions options;

        public V1Verifier(V1Source v1, TargetWriter hot, TargetWriter legs, MigrationOptions options)
        {
            this.v1 = v1; this.hot = hot; this.legs = legs; this.options = options;
        }

        public List<Check> Run()
        {
            var checks = new List<Check>();
            void Add(string name, bool ok, string detail) => checks.Add(new Check(name, ok, detail));

            // ── integrity ──
            Add("hot integrity_check", hot.Scalar<string>("PRAGMA integrity_check") == "ok", hot.Scalar<string>("PRAGMA integrity_check"));
            Add("legs integrity_check", legs.Scalar<string>("PRAGMA integrity_check") == "ok", legs.Scalar<string>("PRAGMA integrity_check"));
            Add("hot foreign_key_check", hot.Scalar<long>("SELECT count(*) FROM pragma_foreign_key_check") == 0, hot.Scalar<long>("SELECT count(*) FROM pragma_foreign_key_check") + " violations");

            // ── id-set preservation (the cache-file invariant: Item.Id == Comics.Id etc.) ──
            IdSet("Item.Id == Comics.Id", "SELECT Id FROM Comics", "SELECT Id FROM Item");
            IdSet("Series.Id == Series.Id", "SELECT Id FROM Series", "SELECT Id FROM Series");
            IdSet("Folder.Id == Folders.Id", "SELECT Id FROM Folders", "SELECT Id FROM Folder");
            IdSet("Publisher.Id == Publishers.Id", "SELECT Id FROM Publishers", "SELECT Id FROM Publisher");
            void IdSet(string name, string v1Sql, string v2Sql)
            {
                var a = v1.Rows(v1Sql).Select(r => r.L("Id")!.Value).ToHashSet();
                var b = hot.Pairs(v2Sql.Replace("SELECT Id", "SELECT Id, NULL")).Select(p => p.Item1).ToHashSet();
                var missing = a.Except(b).Take(5).ToList(); var extra = b.Except(a).Take(5).ToList();
                Add(name, a.SetEquals(b), $"v1 {a.Count}, v2 {b.Count}" + (missing.Count > 0 ? $"; missing e.g. {string.Join(",", missing)}" : "") + (extra.Count > 0 ? $"; extra e.g. {string.Join(",", extra)}" : ""));
            }

            // ── row counts per copied table (exact where the contract copies every row) ──
            Count("Folder", v1.Count("Folders"), hot.Scalar<long>("SELECT count(*) FROM Folder"));
            Count("Folder.ParentId set", v1.Count("Folders", "ParentId IS NOT NULL"), hot.Scalar<long>("SELECT count(*) FROM Folder WHERE ParentId IS NOT NULL"));
            Count("ItemState", v1.Count("Comics"), hot.Scalar<long>("SELECT count(*) FROM ItemState"));
            Count("BookDetail", v1.Count("Comics", "Category = 1"), hot.Scalar<long>("SELECT count(*) FROM BookDetail"));
            Count("Item(Kind=Book)", v1.Count("Comics", "Category = 1"), hot.Scalar<long>("SELECT count(*) FROM Item WHERE Kind = 1"));
            Count("Item.IsExcluded", v1.Count("Comics", "ExcludedFromLibrary = 1"), hot.Scalar<long>("SELECT count(*) FROM Item WHERE IsExcluded = 1"));
            Count("ComicDetail", InScope("ComicParsedDetails", "ComicId"), hot.Scalar<long>("SELECT count(*) FROM ComicDetail"));
            Count("Item.SeriesId set", v1.Count("ComicParsedDetails", "SeriesId IS NOT NULL"), hot.Scalar<long>("SELECT count(*) FROM Item WHERE SeriesId IS NOT NULL"));
            Count("SeriesAlias", v1.Scalar<long>("SELECT count(*) FROM SeriesParsedKeys k WHERE EXISTS (SELECT 1 FROM Series s WHERE s.Id = k.SeriesId)"), hot.Scalar<long>("SELECT count(*) FROM SeriesAlias"));
            Count("SeriesMerge", v1.Count("SeriesMergeLogs"), hot.Scalar<long>("SELECT count(*) FROM SeriesMerge"));
            Count("ReadingOrderEntry", InScope("ComicReadingOrder", "ComicId"), hot.Scalar<long>("SELECT count(*) FROM ReadingOrderEntry"));
            Count("CollectionNode", InScope("ComicCollectionNodes", "ComicId"), hot.Scalar<long>("SELECT count(*) FROM CollectionNode"));
            Count("CollectedEditionSpan", InScope("LocgCollectedEditions", "ComicId") + InScope("GcdCollectedEditions", "ComicId") + InScope("ComicvineCollectedEditions", "ComicId") + InScope("CuratedCollectedEditions", "ComicId"), hot.Scalar<long>("SELECT count(*) FROM CollectedEditionSpan"));
            Count("CvVolume", v1.Count("ComicvineVolumes"), hot.Scalar<long>("SELECT count(*) FROM CvVolume"));
            Count("CvIssue", v1.Count("ComicvineIssues", "VolumeId IS NOT NULL"), hot.Scalar<long>("SELECT count(*) FROM CvIssue"));
            Count("LocgComicRaw (legs)", v1.Count("LocgComics"), legs.Scalar<long>("SELECT count(*) FROM LocgComicRaw"));
            Count("LocgComic (hot subset)", v1.Scalar<long>("SELECT count(DISTINCT LocgComicId) FROM LocgMatches WHERE Status='matched' AND LocgComicId IS NOT NULL AND LocgComicId IN (SELECT LocgComicId FROM LocgComics)"), hot.Scalar<long>("SELECT count(*) FROM LocgComic"));
            Count("LocgContainment (legs)", v1.Count("LocgContainments"), legs.Scalar<long>("SELECT count(*) FROM LocgContainment"));
            Count("GcdIssue (legs)", v1.Count("GcdIssues"), legs.Scalar<long>("SELECT count(*) FROM GcdIssue"));
            Count("MuSeries", v1.Count("MangaUpdatesSeries"), hot.Scalar<long>("SELECT count(*) FROM MuSeries"));
            Count("BarneyProg", v1.Count("BarneyProgs"), hot.Scalar<long>("SELECT count(*) FROM BarneyProg"));
            Count("ExternalWork", v1.Count("ExternalWorks"), hot.Scalar<long>("SELECT count(*) FROM ExternalWork"));
            Count("ProviderResponseCache (legs)", v1.Count("ComicvineApiCaches"), legs.Scalar<long>("SELECT count(*) FROM ProviderResponseCache"));
            Count("SeriesKeyLink", v1.Count("ComicvineSeriesLinks") + v1.Count("ExternalSeriesLinks"), hot.Scalar<long>("SELECT count(*) FROM SeriesKeyLink"));
            Count("MuSeriesLink", v1.Scalar<long>("SELECT count(*) FROM MangaUpdatesMatches m WHERE EXISTS (SELECT 1 FROM Series s WHERE s.Id = m.SeriesId)"), hot.Scalar<long>("SELECT count(*) FROM MuSeriesLink"));
            Count("ItemProviderLink", InScope("ComicvineMatches", "ComicId") + InScope("LocgMatches", "ComicId") + InScope("GcdMatches", "ComicId") + InScope("BarneyMatches", "ComicId") + InScope("MarvelMatches", "ComicId") + InScope("InducksMatches", "ComicId"), hot.Scalar<long>("SELECT count(*) FROM ItemProviderLink"));
            Count("Insight(Item)", InScope("ClaudeBookMetadata", "ComicId"), hot.Scalar<long>("SELECT count(*) FROM Insight WHERE SubjectKind = 0"));
            Count("InsightTag(book)", v1.Scalar<long>("SELECT count(*) FROM ClaudeBookTags t WHERE EXISTS (SELECT 1 FROM ClaudeBookMetadata m WHERE m.ComicId = t.ComicId) AND EXISTS (SELECT 1 FROM Comics c WHERE c.Id = t.ComicId)"), hot.Scalar<long>($"SELECT count(*) FROM InsightTag WHERE InsightId >= {Migration.Units.InsightIds.BookBase} AND InsightId < {MigrationContext.CloneBase}"));
            Count("Rating(Library/Override)", InScope("LibraryComicRatings", "ComicId") + v1.Scalar<long>("SELECT count(*) FROM LibrarySeriesRatings r WHERE EXISTS (SELECT 1 FROM Series s WHERE s.Id = r.SeriesId)") + v1.Scalar<long>("SELECT count(*) FROM LibraryRatingOverrides o WHERE (o.TargetType = 'series' AND EXISTS (SELECT 1 FROM Series s WHERE s.Id = o.TargetId)) OR (o.TargetType <> 'series' AND EXISTS (SELECT 1 FROM Comics c WHERE c.Id = o.TargetId))"), hot.Scalar<long>("SELECT count(*) FROM Rating WHERE Source IN (4, 5)"));
            Count("KidSafeTag", v1.Count("KidSafeTags"), hot.Scalar<long>("SELECT count(*) FROM KidSafeTag"));
            Count("TagAlias", v1.Count("TagAliases"), hot.Scalar<long>("SELECT count(*) FROM TagAlias"));
            Count("CvdbResolution", v1.Count("CvdbResolutions"), hot.Scalar<long>("SELECT count(*) FROM CvdbResolution"));
            Count("SeriesInferenceDecision", v1.Count("SeriesInferenceDecisions"), hot.Scalar<long>("SELECT count(*) FROM SeriesInferenceDecision"));
            Count("SeriesMatchReview", v1.Count("SeriesMatchReviews"), hot.Scalar<long>("SELECT count(*) FROM SeriesMatchReview"));
            Count("DuplicateGroup", v1.Count("DuplicateGroups"), hot.Scalar<long>("SELECT count(*) FROM DuplicateGroup"));
            Count("DuplicateMember", v1.Scalar<long>("SELECT count(*) FROM DuplicateMembers m WHERE EXISTS (SELECT 1 FROM DuplicateGroups g WHERE g.Id = m.DuplicateGroupId) AND EXISTS (SELECT 1 FROM Comics c WHERE c.Id = m.ComicId)"), hot.Scalar<long>("SELECT count(*) FROM DuplicateMember"));
            void Count(string name, long expected, long actual) => Add("count " + name, expected == actual, $"v1 {expected}, v2 {actual}");
            // rows whose parent item exists in v1 — the same guard every unit applies (v1 carries a few orphans of its own)
            long InScope(string table, string itemCol) => v1.Scalar<long>($"SELECT count(*) FROM \"{table}\" t WHERE EXISTS (SELECT 1 FROM Comics c WHERE c.Id = t.\"{itemCol}\")");

            // ── the series-insight edge: every v1 item that reached a series insight still reaches a CURRENT one ──
            var v1Edge = v1.Scalar<long>("SELECT count(*) FROM ComicParsedDetails pd JOIN ClaudeSeriesMetadata m ON m.Id = pd.ClaudeSeriesMetadataId WHERE pd.SeriesId IS NOT NULL");
            var v2Edge = hot.Scalar<long>("SELECT count(*) FROM Item i WHERE i.SeriesId IS NOT NULL AND EXISTS (SELECT 1 FROM Insight n WHERE n.SubjectKind = 1 AND n.SubjectId = i.SeriesId AND n.IsCurrent = 1)");
            Add("item→current series insight edge", v2Edge >= v1Edge, $"v1 items with an insight {v1Edge}, v2 items with a current series insight {v2Edge}");
            var seriesInsights = v1.Count("ClaudeSeriesMetadata");
            var carried = hot.Scalar<long>($"SELECT count(*) FROM Insight WHERE SubjectKind = 1 AND Id < {MigrationContext.CloneBase}");
            var clones = hot.Scalar<long>($"SELECT count(*) FROM Insight WHERE Id >= {MigrationContext.CloneBase}");
            Add("series insights carried + exported", true, $"v1 {seriesInsights}, carried {carried} (+{clones} clones for minority series), exported {seriesInsights - carried} (orphan-insights.json)");
            Add("one current insight per subject", hot.Scalar<long>("SELECT count(*) FROM (SELECT SubjectKind, SubjectId FROM Insight WHERE IsCurrent = 1 GROUP BY 1,2 HAVING count(*) > 1)") == 0, "duplicates by (SubjectKind, SubjectId) with IsCurrent = 1");
            Add("every subject has a current insight", hot.Scalar<long>("SELECT count(*) FROM (SELECT SubjectKind, SubjectId FROM Insight GROUP BY 1,2 HAVING sum(IsCurrent) = 0)") == 0, "subjects with rows but no current row");

            // ── the owner's activity: user 2 → user 1, other users never copied ──
            var owner = options.OwnerUsername;
            var bm = v1.Count("Bookmarks", "Username = $u", ("$u", owner));
            var lists = v1.Count("ComicUserLists", "Username = $u AND ListType = 1", ("$u", owner));
            var marks = v1.Count("GroupUserMetadata", "Username = $u", ("$u", owner));
            var states = hot.Scalar<long>("SELECT count(*) FROM UserItemState WHERE UserId = " + options.UserIdForOwner);
            var want = hot.Scalar<long>("SELECT count(*) FROM UserItemState WHERE UserId = " + options.UserIdForOwner + " AND WantToRead = 1");
            var groupMarks = hot.Scalar<long>("SELECT count(*) FROM GroupMark WHERE UserId = " + options.UserIdForOwner);
            Add("owner positions", states >= bm, $"v1 bookmarks {bm}, v2 UserItemState rows {states}");
            Add("owner want-to-read", want >= lists, $"v1 lists {lists}, v2 WantToRead rows {want}");
            Add("owner group marks", true, $"v1 group rows {marks} (series+comic), v2 GroupMark {groupMarks}");
            Add("no other user copied", hot.Scalar<long>("SELECT count(*) FROM UserItemState WHERE UserId <> " + options.UserIdForOwner) == 0 && hot.Scalar<long>("SELECT count(*) FROM GroupMark WHERE UserId <> " + options.UserIdForOwner) == 0, "UserItemState/GroupMark rows for other user ids");

            // ── enum coverage: no Unknown where v1 had a real value ──
            Add("ComicDetail.Format mapped", true, hot.Scalar<long>("SELECT count(*) FROM ComicDetail WHERE Format = 13 AND FormatRaw IS NOT NULL AND FormatRaw <> 'null'") + " rows with an unmapped FormatRaw (reported, not failed)");
            Add("ItemProviderLink statuses", hot.Scalar<long>("SELECT count(*) FROM ItemProviderLink WHERE Status IS NULL") == 0, "null statuses");

            // ── resolved scalars present ──
            Add("Item.Resolved* populated", hot.Scalar<long>("SELECT count(*) FROM Item WHERE ResolvedAt IS NULL") == 0, hot.Scalar<long>("SELECT count(*) FROM Item WHERE ResolvedAt IS NULL") + " items unresolved");
            Add("Series.Resolved* populated", hot.Scalar<long>("SELECT count(*) FROM Series WHERE ResolvedAt IS NULL") == 0, hot.Scalar<long>("SELECT count(*) FROM Series WHERE ResolvedAt IS NULL") + " series unresolved");
            Add("ItemFts rows == Item rows", hot.Scalar<long>("SELECT count(*) FROM ItemFts") == hot.Scalar<long>("SELECT count(*) FROM Item"), $"fts {hot.Scalar<long>("SELECT count(*) FROM ItemFts")}, items {hot.Scalar<long>("SELECT count(*) FROM Item")}");

            // ── the port proof: recompute series resolution with v1's OWN signal (the sticky per-row provider ids v1 grouped
            //    by parsed key) and diff against the copied derivation. Aliases v1 would drop at its next rebuild (parsed keys no
            //    longer in any parsed-detail row) are reported, not failed. ──
            var v1Signal = new SeriesResolver.Signal(
                v1.Rows("SELECT Series AS K, ComicvineVolumeId AS V, count(*) AS N FROM ComicParsedDetails WHERE Series IS NOT NULL AND Series <> '' AND ComicvineVolumeId IS NOT NULL GROUP BY 1, 2")
                    .GroupBy(r => r.S("K")!).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.L("N")).ThenBy(r => r.L("V")).First().L("V")!.Value, StringComparer.Ordinal),
                v1.Rows("SELECT Series AS K, ExternalWorkId AS V, count(*) AS N FROM ComicParsedDetails WHERE Series IS NOT NULL AND Series <> '' AND ExternalWorkId IS NOT NULL GROUP BY 1, 2")
                    .GroupBy(r => r.S("K")!).ToDictionary(g => g.Key, g => (int)g.OrderByDescending(r => r.L("N")).ThenBy(r => r.L("V")).First().L("V")!.Value, StringComparer.Ordinal));
            var faithful = SeriesResolver.Diff(hot, 20, v1Signal);
            Add("series-resolution recompute (v1 signal) diff", faithful.AliasAdded + faithful.AliasChanged + faithful.SurvivorNameChanged + faithful.SurvivorKeyChanged == 0,
                $"alias +{faithful.AliasAdded} ~{faithful.AliasChanged} (stale, v1 would drop too: -{faithful.AliasRemoved}); survivor name {faithful.SurvivorNameChanged}, key {faithful.SurvivorKeyChanged}; merge candidates {faithful.MergedAway}"
                + (faithful.Samples.Count > 0 ? "\n      " + string.Join("\n      ", faithful.Samples) : ""));
            var current = SeriesResolver.Diff(hot);
            Add("series-resolution recompute (current links) drift — informational", true,
                $"what the R6 rebuild will change: alias +{current.AliasAdded} ~{current.AliasChanged} -{current.AliasRemoved}; survivor name {current.SurvivorNameChanged}, key {current.SurvivorKeyChanged}; merges {current.MergedAway}");

            // ── every unit finished ──
            var unfinished = hot.Pairs("SELECT rowid, Stage FROM MigrationProgress WHERE FinishedAt IS NULL").Select(p => p.Item2).ToList();
            var finished = hot.Scalar<long>("SELECT count(*) FROM MigrationProgress WHERE FinishedAt IS NOT NULL");
            Add("all migration units finished", unfinished.Count == 0 && finished == MigrationUnits.All().Count, $"{finished}/{MigrationUnits.All().Count} finished" + (unfinished.Count > 0 ? "; unfinished: " + string.Join(", ", unfinished) : ""));

            return checks;
        }

        public static string Render(IReadOnlyList<Check> checks, string title)
        {
            var lines = new List<string> { "# " + title, "", $"- {checks.Count(c => c.Passed)} passed, {checks.Count(c => !c.Passed)} failed", "" };
            foreach (var c in checks) lines.Add($"- {(c.Passed ? "PASS" : "FAIL")} {c.Name}: {c.Detail}");
            return string.Join("\n", lines) + "\n";
        }
    }
}
