using MovieTheater.Books.Db;

namespace MovieTheater.Books.Migration.Units
{
    /// <summary>
    /// The three per-user v1 tables → UserItemState / GroupMark / Rating(User), for the OWNER account only
    /// (decision 5: every other standalone-site user is counted and reported, never copied). The three units
    /// merge into one UserItemState row per item through the writer's column-scoped upsert.
    /// </summary>
    public sealed class BookmarksUnit : StageUnit
    {
        public override string Stage => "user-activity";
        public override string? SourceTable => "Bookmarks";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            if (!string.Equals(r.S("Username"), ctx.Options.OwnerUsername, StringComparison.Ordinal)) { c.Skipped++; c.Bump("other-user"); return; }
            if (!ctx.ItemExists(r.L("ComicId"))) { c.Unmapped++; c.Bump("item-missing"); return; }
            hot.Upsert("UserItemState", new
            {
                UserId = ctx.Options.UserIdForOwner, ItemId = r.Int("ComicId"), LastPage = r.Int("LastPage"), LastSpineItemIndex = r.I("LastSpineItemIndex"), LastScrollPercent = r.D("LastScrollPercent"),
                Status = Transforms.ReadStatusOf(r.I("Status")), HiddenFromHistory = r.B("HiddenFromHistory"), UpdatedAt = r.At("UpdatedAt"),
            });
            c.Inserted++;
        }
    }

    public sealed class UserListsUnit : StageUnit
    {
        public override string Stage => "user-activity";
        public override string? SourceTable => "ComicUserLists";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            if (!string.Equals(r.S("Username"), ctx.Options.OwnerUsername, StringComparison.Ordinal)) { c.Skipped++; c.Bump("other-user"); return; }
            if (!ctx.ItemExists(r.L("ComicId"))) { c.Unmapped++; c.Bump("item-missing"); return; }
            if (r.Int("ListType") != 1) { c.Unmapped++; c.Bump("unknown-list-type"); return; }
            var itemId = r.Int("ComicId");
            var exists = hot.Scalar<long>("SELECT count(*) FROM UserItemState WHERE UserId=$u AND ItemId=$i", ("$u", ctx.Options.UserIdForOwner), ("$i", itemId)) > 0;
            if (exists) hot.Update("UserItemState", "ItemId", itemId, new { WantToRead = true });
            else hot.Upsert("UserItemState", new { UserId = ctx.Options.UserIdForOwner, ItemId = itemId, LastPage = 0, Status = ReadStatus.Unread, WantToRead = true, Favorite = false, HiddenFromHistory = false, UpdatedAt = r.At("AddedAt") });
            c.Inserted++;
        }
    }

    public sealed class GroupMarksUnit : StageUnit
    {
        public override string Stage => "user-activity";
        public override string? SourceTable => "GroupUserMetadata";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            if (!string.Equals(r.S("Username"), ctx.Options.OwnerUsername, StringComparison.Ordinal)) { c.Skipped++; c.Bump("other-user"); return; }
            var userId = ctx.Options.UserIdForOwner;
            var type = (r.T("GroupType") ?? "").ToLowerInvariant();
            var key = r.T("GroupKey") ?? "";
            if (type == "comic")
            {
                if (!long.TryParse(key, out var itemId) || !ctx.ItemExists(itemId)) { c.Unmapped++; c.Bump("item-missing"); return; }
                var exists = hot.Scalar<long>("SELECT count(*) FROM UserItemState WHERE UserId=$u AND ItemId=$i", ("$u", userId), ("$i", itemId)) > 0;
                if (exists)
                {
                    // flags OR across the three v1 stores (the standalone merge rule): a mark never clears what a list set
                    var want = r.B("WantToRead") || hot.Scalar<long>("SELECT WantToRead FROM UserItemState WHERE UserId=$u AND ItemId=$i", ("$u", userId), ("$i", itemId)) != 0;
                    hot.Update("UserItemState", "ItemId", (int)itemId, new { Favorite = r.B("IsFavorite"), WantToRead = want });
                }
                else hot.Upsert("UserItemState", new { UserId = userId, ItemId = (int)itemId, LastPage = 0, Status = ReadStatus.Unread, WantToRead = r.B("WantToRead"), Favorite = r.B("IsFavorite"), HiddenFromHistory = false, UpdatedAt = r.At("UpdatedAt") });
                if (r.I("Rating") is int rating)
                {
                    hot.Upsert("Rating", new { TargetKind = SubjectKind.Item, TargetId = (int)itemId, Source = RatingSource.User, Value = (int?)rating, RawValue = (double?)rating, RawScale = "0-100", Count = (int?)null, Note = r.T("Notes"), IsOverride = false, ModelId = "user:" + userId, GeneratedAt = r.At("UpdatedAt") });
                    c.Bump("user-ratings");
                }
                else if (r.T("Notes") != null) c.Bump("notes-dropped");
                c.Inserted++;
                return;
            }
            GroupType gt = type switch
            {
                "series" => GroupType.Series, "volume" => GroupType.Volume, "collection" => GroupType.Collection, "publisher" => GroupType.Publisher, "decade" => GroupType.Decade,
                _ => (GroupType)(-1),
            };
            if ((int)gt < 0) { c.Unmapped++; c.Bump("unknown-group-type"); return; }
            if (gt == GroupType.Series)
            {
                // series keys are SeriesId strings in v2; v1 carried both ids and names
                if (long.TryParse(key, out var sid)) { if (!ctx.SeriesExists(sid)) { c.Unmapped++; c.Bump("series-missing"); return; } }
                else if (ctx.SeriesByName(key) is long byName) { key = byName.ToString(); c.Bump("series-by-name"); }
                else { c.Unmapped++; c.Bump("series-name-unresolved"); return; }
            }
            hot.Upsert("GroupMark", new { UserId = userId, GroupType = gt, GroupKey = key, IsRead = r.B("IsRead"), WantToRead = r.B("WantToRead"), IsFavorite = r.B("IsFavorite"), Rating = r.I("Rating"), Notes = r.T("Notes"), UpdatedAt = r.At("UpdatedAt") });
            c.Inserted++;
        }
    }

    public sealed class SiteSettingsUnit : StageUnit
    {
        public override string Stage => "system-state";
        public override string? SourceTable => "SiteSettings";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            hot.Upsert("SystemState", new { Key = r.S("Key") ?? "", Value = r.S("Value") });
            c.Inserted++;
        }
    }

    /// <summary>v1 SystemState: the fold/resolution fingerprints land on their DerivedTable rows; anything else stays KV.</summary>
    public sealed class SystemStateUnit : StageUnit
    {
        public override string Stage => "system-state";
        public override string? SourceTable => "SystemState";
        public override void Transform(V1Row r, MigrationContext ctx, TargetWriter hot, TargetWriter legs, UnitCounts c)
        {
            var key = r.S("Key") ?? "";
            var derived = Transforms.DerivedTableForFingerprint(key);
            if (derived != null)
            {
                var job = DerivedTables.All.First(d => d.Name == derived).RebuildJob;
                hot.Upsert("DerivedTable", new { Name = derived, RebuildJob = job, InputFingerprint = "v1:" + key + "=" + (r.S("Value") ?? "") });
                c.Bump("fingerprints");
            }
            else hot.Upsert("SystemState", new { Key = key, Value = r.S("Value") });
            c.Inserted++;
        }
    }
}
