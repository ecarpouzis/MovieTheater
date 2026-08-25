using MovieTheater.Books.Db;

namespace MovieTheater.Books.Migration.Units
{
    public sealed class ReadingOrderUnit : StageUnit
    {
        public override string Stage => "reading-order";
        public override string? SourceTable => "ComicReadingOrder";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var id = r.L("ComicId");
            if (!ctx.ItemExists(id)) { c.Unmapped++; c.Bump("item-missing"); return; }
            // GroupKey is a canonical series key ("cv:19752") or '' (= the item's own resolved series)
            var key = r.T("GroupKey");
            var seriesId = key == null ? ctx.ItemSeriesId(id!.Value) : ctx.SeriesByCanonicalKey(key);
            if (key != null && seriesId == null) { seriesId = ctx.ItemSeriesId(id!.Value); c.Bump(seriesId == null ? "groupkey-unresolved" : "groupkey-fallback-own-series"); }
            hot.Upsert("ReadingOrderEntry", new
            {
                ItemId = (int)id!.Value, SeriesId = (int?)seriesId, ReadTier = r.I("ReadTier"), ReadNumber = r.D("ReadNumber"), ReadNumberSuffix = r.D("ReadNumberSuffix"),
                ReadDate = r.T("ReadDate"), ReadDatePrecision = Transforms.PrecisionOf(r.S("ReadDatePrecision")), ReadIndex = r.I("ReadIndex"), ReadCount = r.Int("ReadCount"),
                Source = Transforms.ReadingOrderSourceOf(r.S("Source")), Confidence = Transforms.ConfidenceOf(r.S("Confidence")), Notes = r.T("Notes"), ComputedAt = r.At("ComputedAt"),
            });
            c.Inserted++;
        }
    }

    public sealed class CollectionNodesUnit : StageUnit
    {
        public override string Stage => "collection-nodes";
        public override string? SourceTable => "ComicCollectionNodes";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var id = r.L("ComicId");
            if (!ctx.ItemExists(id)) { c.Unmapped++; c.Bump("item-missing"); return; }
            var parent = r.L("ParentComicId");
            if (parent != null && !ctx.ItemExists(parent)) { parent = null; c.Bump("parent-missing"); }
            hot.Upsert("CollectionNode", new
            {
                ItemId = (int)id!.Value, SeriesId = r.I("SeriesId"), Level = (CollectionLevel)Math.Clamp(r.Int("CollectionLevel"), 0, 3), TrackRole = Transforms.TrackRoleOf(r.S("TrackRole")),
                SpanStart = r.I("SpanStart"), SpanEnd = r.I("SpanEnd"), ContainsCount = r.Int("ContainsCount"), ParentItemId = (int?)parent, SpanSource = Transforms.SpanSourceOf(r.S("SpanSource")), SpanLabel = r.T("SpanLabel"),
            });
            c.Inserted++;
        }
    }

    /// <summary>The four collected-edition tables → CollectedEditionSpan(Source).</summary>
    public sealed class CollectedEditionsUnit : StageUnit
    {
        private readonly string table;
        private readonly EditionSource source;
        public CollectedEditionsUnit(string table, EditionSource source) { this.table = table; this.source = source; }
        public override string Stage => "collected-editions";
        public override string? SourceTable => table;
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var id = r.L("ComicId");
            if (!ctx.ItemExists(id)) { c.Unmapped++; c.Bump("item-missing"); return; }
            string? providerRef = source switch
            {
                EditionSource.Cv => r.S("ComicvineVolumeId"),
                EditionSource.Gcd => r.S("SourceSeries"),
                EditionSource.Locg => r.S("LocgComicId"),
                _ => null,
            };
            string? note = source switch
            {
                EditionSource.Gcd => Join(r.T("Note"), r.T("MatchBy") is string mb ? "match-by: " + mb : null),
                EditionSource.Locg => r.L("ContainedCount") is long cc ? "contained: " + cc : null,
                _ => r.Has("Note") ? r.T("Note") : null,
            };
            hot.Upsert("CollectedEditionSpan", new
            {
                ItemId = (int)id!.Value, Source = source, SeriesId = r.I("SeriesId"), IssueStart = r.D("IssueStart"), IssueEnd = r.D("IssueEnd"), EditionTitle = r.T("EditionTitle"),
                ProviderRef = providerRef, Contiguous = r.Has("Contiguous") && r.B("Contiguous"), Confidence = r.D("Confidence"), Note = note,
                CreatedAt = r.Has("CreatedAt") ? r.At("CreatedAt") : r.At("ScrapedAt"),
            });
            c.Inserted++;
        }
        private static string? Join(string? a, string? b) => a == null ? b : b == null ? a : a + "; " + b;
    }

    public sealed class CvVolumesUnit : StageUnit
    {
        public override string Stage => "cv";
        public override string? SourceTable => "ComicvineVolumes";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var id = r.Int("ComicvineId");
            hot.Upsert("CvVolume", new
            {
                Id = id, Name = r.S("Name"), StartYear = r.I("StartYear"), PublisherName = r.S("PublisherName"), CountOfIssues = r.I("CountOfIssues"), Deck = r.S("Deck"),
                Description = r.S("Description"), ImageUrl = r.S("ImageUrl"), SiteDetailUrl = r.S("SiteDetailUrl"), FetchedAt = r.At("FetchedAt"),
            });
            legs.Upsert("CvVolumeRaw", new { CvVolumeId = id, ConceptsJson = r.S("ConceptsJson"), CharactersJson = r.S("CharactersJson"), LocationsJson = r.S("LocationsJson"), ObjectsJson = r.S("ObjectsJson"), TeamsJson = r.S("TeamsJson") });
            c.Inserted++;
        }
    }

    public sealed class CvIssuesUnit : StageUnit
    {
        public override string Stage => "cv";
        public override string? SourceTable => "ComicvineIssues";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            if (r.L("VolumeId") == null) { c.Unmapped++; c.Bump("volume-null"); return; }
            if (!ctx.CvVolumeExists(r.L("VolumeId"))) c.Bump("volume-not-fetched");
            hot.Upsert("CvIssue", new
            {
                Id = r.Int("ComicvineId"), VolumeId = r.Int("VolumeId"), Name = r.S("Name"), IssueNumber = r.S("IssueNumber"), CoverDate = r.T("CoverDate"), StoreDate = r.T("StoreDate"),
                Deck = r.S("Deck"), Description = r.S("Description"), ImageUrl = r.S("ImageUrl"), SiteDetailUrl = r.S("SiteDetailUrl"), FetchedAt = r.At("FetchedAt"),
            });
            c.Inserted++;
        }
    }

    /// <summary>
    /// LocgComics → LocgComicRaw + LocgCreatorRaw (legs, every row) and LocgComic + ItemCredit(Locg) (hot, only the
    /// rows some item is matched to — the 83k/157k split of v2-model.md §15).
    /// </summary>
    public sealed class LocgUnit : StageUnit
    {
        public override string Stage => "locg";
        public override string? SourceTable => "LocgComics";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var id = r.Int("LocgComicId");
            legs.Upsert("LocgComicRaw", new
            {
                LocgComicId = id, LocgSeriesId = r.I("LocgSeriesId"), SeriesName = r.S("SeriesName"), Title = r.S("Title"), IssueNumber = r.S("IssueNumber"), Format = r.S("Format"),
                ReleaseDate = r.T("ReleaseDate"), CoverDate = r.T("CoverDate"), PageCount = r.I("PageCount"), Description = r.S("Description"), CommunityRating = r.D("CommunityRating"),
                RatingCount = r.I("RatingCount"), IsKey = r.B("IsKey"), KeyType = r.T("KeyType"), KeyReason = r.T("KeyReason"), Isbn = r.T("Isbn"), Upc = r.T("Upc"), DistributorSku = r.T("DistributorSku"),
                CoverPrice = r.T("CoverPrice"), EstimatedValue = r.T("EstimatedValue"), CoverUrl = r.T("CoverUrl"), Url = r.T("Url"), StoryCount = r.I("StoryCount"), StoryIdsJson = r.S("StoryIdsJson"), ScrapedAt = r.At("ScrapedAt"),
            });
            var creators = Transforms.ParseCreators(r.S("CreatorsJson"));
            for (var i = 0; i < creators.Count; i++)
                legs.Upsert("LocgCreatorRaw", new { LocgComicId = id, Ordinal = i, Role = creators[i].Role, Name = creators[i].Name, PeopleId = creators[i].PeopleId });
            if (ctx.LocgMatched(id))
            {
                hot.Upsert("LocgComic", new
                {
                    LocgComicId = id, LocgSeriesId = r.I("LocgSeriesId"), SeriesName = r.S("SeriesName"), Title = r.S("Title"), IssueNumber = r.S("IssueNumber"), Format = r.S("Format"),
                    CoverDate = r.T("CoverDate"), PageCount = r.I("PageCount"), Description = r.S("Description"), CommunityRating = r.D("CommunityRating"), RatingCount = r.I("RatingCount"),
                    IsKey = r.B("IsKey"), KeyType = r.T("KeyType"), Isbn = r.T("Isbn"), Upc = r.T("Upc"), CoverPrice = r.T("CoverPrice"), CoverUrl = r.T("CoverUrl"), StoryCount = r.I("StoryCount"), ScrapedAt = r.At("ScrapedAt"),
                });
                c.Bump("hot");
                foreach (var itemId in ctx.ItemsForLocgComic(id))
                {
                    if (!ctx.ItemExists(itemId)) continue;
                    for (var i = 0; i < creators.Count; i++)
                        hot.Upsert("ItemCredit", new { ItemId = (int)itemId, Source = TagSource.Locg, Ordinal = i, Role = creators[i].Role, Name = creators[i].Name, NormalizedName = Transforms.NormalizeName(creators[i].Name), ProviderPersonId = creators[i].PeopleId });
                    c.Bump("credits", creators.Count);
                }
            }
            c.Inserted++;
        }
    }

    public sealed class MuUnit : StageUnit
    {
        public override string Stage => "mu";
        public override string? SourceTable => "MangaUpdatesSeries";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var id = r.L("MuSeriesId") ?? throw new InvalidOperationException("MangaUpdatesSeries row without MuSeriesId");
            hot.Upsert("MuSeries", new
            {
                Id = id, Title = r.S("Title"), Year = r.I("Year"), Type = r.T("Type"), Status = r.T("Status"), Completed = r.B("Completed"), Description = r.S("Description"),
                BayesianRating = r.D("BayesianRating"), Url = r.T("Url"), ScrapedAt = r.At("ScrapedAt"),
            });
            legs.Upsert("MuSeriesRaw", new { MuSeriesId = id, GenresJson = r.S("GenresJson"), CategoriesJson = r.S("CategoriesJson"), RawJson = r.S("RawJson") });
            c.Inserted++;
        }
    }

    public sealed class BarneyUnit : StageUnit
    {
        public override string Stage => "barney";
        public override string? SourceTable => "BarneyProgs";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("BarneyProg", new { ProgNo = r.Int("ProgNo"), CoverDate = r.T("CoverDate"), Price = r.T("Price"), StripsJson = r.S("StripsJson"), ScrapedAt = r.At("ScrapedAt") });
            c.Inserted++;
        }
    }

    public sealed class ExternalWorksUnit : StageUnit
    {
        public override string Stage => "external";
        public override string? SourceTable => "ExternalWorks";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("ExternalWork", new
            {
                Id = r.Int("Id"), Provider = r.S("Provider") ?? "openlibrary", ProviderKey = r.S("ProviderKey"), Title = r.S("Title"), Authors = r.S("Authors"), Publisher = r.S("Publisher"),
                FirstPublishYear = r.I("FirstPublishYear"), Description = r.S("Description"), CoverImageUrl = r.T("CoverImageUrl"), Isbn = r.T("Isbn"), InfoUrl = r.T("InfoUrl"), FetchedAt = r.At("FetchedAt"),
            });
            // the External fold (subjects -> canonical tags) runs HERE because SubjectsJson lives only in the legs file afterwards
            var folded = Resolve.TagFolds.FoldSubjects(r.S("SubjectsJson"));
            if (folded.Count > 0)
                foreach (var seriesId in ctx.SeriesForExternalWork(r.L("Id")!.Value))
                {
                    foreach (var tag in folded) hot.Upsert("SeriesTag", new { SeriesId = (int)seriesId, Category = Resolve.TagFolds.FoldedCategory, Value = tag, Source = TagSource.External });
                    c.Bump("folded-tags", folded.Count);
                }
            c.Inserted++;
        }
    }

    /// <summary>A v1 table copied column-for-column into a legs table (after the contract's renames).</summary>
    public sealed class LegsCopyUnit : StageUnit
    {
        private readonly string source, target;
        private readonly Func<V1Row, MigrationContext, object?> project;
        public LegsCopyUnit(string source, string target, Func<V1Row, MigrationContext, object?> project) { this.source = source; this.target = target; this.project = project; }
        public override string Stage => "legs";
        public override string? SourceTable => source;
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var values = project(r, ctx);
            if (values == null) { c.Unmapped++; return; }
            legs.Upsert(target, values);
            c.Inserted++;
        }
    }

    /// <summary>ComicvineSeriesLinks / ExternalSeriesLinks → SeriesKeyLink (keyed by the parsed key) + LinkCandidates (legs).</summary>
    public sealed class SeriesKeyLinksUnit : StageUnit
    {
        private readonly string table;
        private readonly Provider provider;
        private readonly string keyCol;
        public SeriesKeyLinksUnit(string table, Provider provider, string keyCol) { this.table = table; this.provider = provider; this.keyCol = keyCol; }
        public override string Stage => "series-links";
        public override string? SourceTable => table;
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var key = r.S("SeriesName") ?? "";
            var candidates = r.S("CandidatesJson");
            hot.Upsert("SeriesKeyLink", new
            {
                ParsedKey = key, Provider = provider, ProviderKey = r.I(keyCol), Status = Transforms.LinkStatusOfCvInt(r.I("Status")), Score = r.I("MatchScore"),
                StoredTopScore = Transforms.TopScore(candidates), AttemptCount = r.Int("AttemptCount"), AttemptedAt = r.At("AttemptedAt"), Error = r.T("ErrorMessage"),
            });
            if (candidates != null) { legs.Upsert("LinkCandidates", new { Scope = SubjectKind.Series, Key = key, Provider = provider, CandidatesJson = candidates }); c.Bump("candidates"); }
            c.Inserted++;
        }
    }

    /// <summary>MangaUpdatesMatches → MuSeriesLink (+ Series.MuSeriesId when matched) + LinkCandidates.</summary>
    public sealed class MuLinksUnit : StageUnit
    {
        public override string Stage => "series-links";
        public override string? SourceTable => "MangaUpdatesMatches";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var seriesId = r.L("SeriesId");
            if (!ctx.SeriesExists(seriesId)) { c.Unmapped++; c.Bump("series-missing"); return; }
            var status = Transforms.LinkStatusOfText(r.S("Status"));
            var mu = ctx.MuSeriesExists(r.L("MuSeriesId")) ? r.L("MuSeriesId") : null;
            hot.Upsert("MuSeriesLink", new { SeriesId = (int)seriesId!.Value, MuSeriesId = mu, Status = status, Method = r.T("MatchMethod"), Confidence = r.D("Confidence"), MatchedKey = r.T("MatchedKey"), CreatedAt = r.At("CreatedAt") });
            if (status == LinkStatus.Matched && mu != null)
            {
                hot.Update("Series", "Id", (int)seriesId.Value, new { MuSeriesId = mu });
                // the MU fold (genres + whitelisted categories -> canonical tags); the JSON lives only in the legs file afterwards
                var (genres, categories) = ctx.MuJson(mu.Value);
                var folded = Resolve.TagFolds.FoldMu(genres, categories);
                foreach (var tag in folded) hot.Upsert("SeriesTag", new { SeriesId = (int)seriesId.Value, Category = Resolve.TagFolds.FoldedCategory, Value = tag, Source = TagSource.Mu });
                c.Bump("folded-tags", folded.Count);
            }
            if (r.S("CandidatesJson") is string cj) { legs.Upsert("LinkCandidates", new { Scope = SubjectKind.Series, Key = seriesId.Value.ToString(), Provider = Provider.Mu, CandidatesJson = cj }); c.Bump("candidates"); }
            c.Inserted++;
        }
    }

    /// <summary>The six item-level match tables → ItemProviderLink(Provider) (+ LinkCandidates for CV).</summary>
    public sealed class ItemLinksUnit : StageUnit
    {
        private readonly string table;
        private readonly Provider provider;
        public ItemLinksUnit(string table, Provider provider) { this.table = table; this.provider = provider; }
        public override string Stage => "item-links";
        public override string? SourceTable => table;
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var id = r.L("ComicId");
            if (!ctx.ItemExists(id)) { c.Unmapped++; c.Bump("item-missing"); return; }
            var itemId = (int)id!.Value;
            switch (provider)
            {
                case Provider.Cv:
                {
                    var cj = r.S("CandidatesJson");
                    hot.Upsert("ItemProviderLink", new
                    {
                        ItemId = itemId, Provider = provider, ProviderKey = r.S("ComicvineIssueId"), SecondaryKey = r.S("ComicvineVolumeId"), Status = Transforms.LinkStatusOfCvInt(r.I("Status")),
                        Method = (string?)null, MatchedKey = (string?)null, Confidence = (double?)null, Quality = LinkQuality.Unknown, StoredTopScore = Transforms.TopScore(cj), Applied = r.B("Applied"),
                        AttemptCount = r.Int("AttemptCount"), AttemptedAt = r.At("LastAttemptedAt"), Error = r.T("ErrorMessage"),
                    });
                    if (cj != null) { legs.Upsert("LinkCandidates", new { Scope = SubjectKind.Item, Key = itemId.ToString(), Provider = provider, CandidatesJson = cj }); c.Bump("candidates"); }
                    break;
                }
                case Provider.Locg:
                {
                    var rawStatus = r.T("Status");
                    var rawQuality = r.T("MatchQuality");
                    var status = Transforms.LinkStatusOfText(rawStatus);
                    var method = r.T("MatchMethod");
                    if (string.Equals(rawQuality, "span-corroborated", StringComparison.OrdinalIgnoreCase)) { method = method == null ? "span-corroborated" : method + ";span-corroborated"; c.Bump("span-corroborated"); }
                    var error = r.T("ErrorMessage") ?? (status == LinkStatus.Cleared ? rawStatus : null);
                    hot.Upsert("ItemProviderLink", new
                    {
                        ItemId = itemId, Provider = provider, ProviderKey = r.S("LocgComicId"), SecondaryKey = (string?)null, Status = status, Method = method, MatchedKey = r.T("MatchedKey"),
                        Confidence = r.D("Confidence"), Quality = Transforms.QualityOf(rawQuality), StoredTopScore = (int?)null, Applied = r.B("Applied"), AttemptCount = 0, AttemptedAt = r.At("LastScrapedAt"), Error = error,
                    });
                    if (rawQuality == null) c.Bump("quality-null");
                    break;
                }
                case Provider.Gcd:
                {
                    var gcdStatus = Transforms.LinkStatusOfText(r.S("Status"));
                    hot.Upsert("ItemProviderLink", new
                    {
                        ItemId = itemId, Provider = provider, ProviderKey = r.S("GcdIssueId"), SecondaryKey = r.S("GcdSeriesId"), Status = gcdStatus, Method = r.T("MatchMethod"),
                        MatchedKey = r.T("MatchedKey"), Confidence = r.D("Confidence"), Quality = LinkQuality.Unknown, StoredTopScore = (int?)null, Applied = r.B("Applied"), AttemptCount = 0, AttemptedAt = r.At("CreatedAt"), Error = r.T("ErrorMessage"),
                    });
                    // the GCD story-genre fold; StoryGenres lives only in the legs file afterwards
                    if (gcdStatus == LinkStatus.Matched && r.L("GcdIssueId") is long gcdIssueId)
                    {
                        var folded = Resolve.TagFolds.FoldGcd(ctx.GcdStoryGenres(gcdIssueId));
                        foreach (var tag in folded) hot.Upsert("ItemTag", new { ItemId = itemId, Category = Resolve.TagFolds.FoldedCategory, Value = tag, Source = TagSource.Gcd });
                        c.Bump("folded-tags", folded.Count);
                    }
                    break;
                }
                case Provider.Barney:
                    hot.Upsert("ItemProviderLink", new
                    {
                        ItemId = itemId, Provider = provider, ProviderKey = r.S("ProgNo"), SecondaryKey = (string?)null, Status = LinkStatus.Matched, Method = r.T("MatchMethod"), MatchedKey = (string?)null,
                        Confidence = (double?)null, Quality = LinkQuality.Unknown, StoredTopScore = (int?)null, Applied = true, AttemptCount = 0, AttemptedAt = r.At("CreatedAt"), Error = (string?)null,
                    });
                    break;
                case Provider.Marvel:
                    hot.Upsert("ItemProviderLink", new
                    {
                        ItemId = itemId, Provider = provider, ProviderKey = r.S("MarvelIssueId"), SecondaryKey = (string?)null, Status = LinkStatus.Matched, Method = r.T("MatchMethod"), MatchedKey = (string?)null,
                        Confidence = r.D("Confidence"), Quality = LinkQuality.Unknown, StoredTopScore = (int?)null, Applied = true, AttemptCount = 0, AttemptedAt = r.At("CreatedAt"), Error = (string?)null,
                    });
                    break;
                case Provider.Inducks:
                    hot.Upsert("ItemProviderLink", new
                    {
                        ItemId = itemId, Provider = provider, ProviderKey = r.S("IssueCode"), SecondaryKey = r.S("PublicationCode"), Status = Transforms.LinkStatusOfText(r.S("Status")), Method = r.T("MatchMethod"),
                        MatchedKey = (string?)null, Confidence = r.D("Confidence"), Quality = LinkQuality.Unknown, StoredTopScore = (int?)null, Applied = true, AttemptCount = 0, AttemptedAt = r.At("CreatedAt"), Error = (string?)null,
                    });
                    break;
                default:
                    throw new InvalidOperationException($"no item-link shape for {provider}");
            }
            c.Inserted++;
        }
    }
}
