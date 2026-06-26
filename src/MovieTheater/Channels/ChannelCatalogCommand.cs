using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Channels
{
    /// <summary>
    /// Upserts the code-defined <see cref="ChannelCatalog"/> into the Channel table, keyed by the stable
    /// <see cref="Db.Channel.CatalogKey"/>. Idempotent: creates missing channels, updates changed ones
    /// (pruning their not-yet-aired schedule when the filter/strategy changed, like the admin Save path),
    /// disables catalog channels removed from code, and never touches hand-made channels (null CatalogKey).
    /// Dry-run unless <c>--apply</c>. <c>--pool-report</c> additionally counts each channel's eligible set
    /// (slow: one query per channel against the live DB) so thin/empty channels surface before they ship.
    /// </summary>
    [Command("channel-catalog", Description = "Upsert the code-defined channel catalog (dry-run unless --apply).")]
    public class ChannelCatalogCommand : BasicDICommand, ICommand
    {
        [CommandOption("apply", Description = "Write changes. Without it, this is a dry run.")]
        public bool Apply { get; set; }

        [CommandOption("pool-report", Description = "Count each channel's eligible set and flag thin/empty ones.")]
        public bool PoolReport { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public ChannelCatalogCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        // Deterministic FNV-1a hash so a channel's shuffle seed is stable across runs (unlike string.GetHashCode).
        private static int StableSeed(string key)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in key) { h ^= c; h *= 16777619; }
                return (int)(h & 0x7FFFFFFF);
            }
        }

        // Lower-case + strip diacritics so "Toshiro Mifune" matches the DB's "Toshirô Mifune".
        private static string Fold(string s)
        {
            var d = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (var c in d)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var db = await dbFactory.CreateDbContextAsync();

            var genres = await db.Genres.ToDictionaryAsync(g => g.Name, g => g.Id, StringComparer.OrdinalIgnoreCase);
            var peopleRows = await db.People.Where(p => p.DisplayName != null)
                .Select(p => new { p.Id, p.DisplayName }).ToListAsync();
            var peopleByName = new Dictionary<string, List<int>>();
            foreach (var p in peopleRows)
            {
                var k = Fold(p.DisplayName!);
                if (!peopleByName.TryGetValue(k, out var l)) peopleByName[k] = l = new List<int>();
                l.Add(p.Id);
            }

            var warnings = new List<string>();

            ChannelFilter Resolve(ChannelDef def)
            {
                var f = def.Filter; // mutated in place — defs are single-use
                f.Kinds = def.Kinds;
                f.GenreMode = def.GenreModeAll ? "all" : "any";
                f.GenreIds = def.GenreNames.Select(n =>
                {
                    if (genres.TryGetValue(n, out var id)) return id;
                    warnings.Add($"  [{def.Key}] unknown genre '{n}'");
                    return -1;
                }).Where(id => id > 0).ToList();
                f.Credits = def.CreditNames.Select(r =>
                {
                    var ids = r.Names.SelectMany(n =>
                    {
                        if (peopleByName.TryGetValue(Fold(n), out var l)) return l;
                        warnings.Add($"  [{def.Key}] unknown person '{n}'");
                        return Enumerable.Empty<int>();
                    }).Distinct().ToList();
                    return new CreditRule { Role = r.Role, PersonIds = ids };
                }).Where(cr => cr.PersonIds.Count > 0).ToList();
                return f;
            }

            var existing = await db.Channels.ToListAsync();
            var byKey = existing.Where(c => c.CatalogKey != null)
                .ToDictionary(c => c.CatalogKey!, StringComparer.OrdinalIgnoreCase);

            int created = 0, updated = 0, unchanged = 0, disabled = 0, sort = 0;
            var now = DateTime.UtcNow;
            var resolvedJson = new Dictionary<string, string>();

            foreach (var def in ChannelCatalog.All)
            {
                var filterJson = Resolve(def).ToJson();
                resolvedJson[def.Key] = filterJson;
                sort++;

                if (byKey.TryGetValue(def.Key, out var ch))
                {
                    bool filterChanged = ch.FilterJson != filterJson
                        || (ch.ScheduleStrategy ?? "") != def.Strategy
                        || (ch.RotationJson ?? "") != (def.RotationJson ?? "");
                    bool changed = filterChanged || ch.Name != def.Name || ch.Description != def.Description
                        || ch.Category != def.Group || !ch.Enabled
                        || ch.SeasonStartMonth != def.SeasonStartMonth || ch.SeasonStartDay != def.SeasonStartDay
                        || ch.SeasonEndMonth != def.SeasonEndMonth || ch.SeasonEndDay != def.SeasonEndDay;

                    if (changed)
                    {
                        ch.Name = def.Name; ch.Description = def.Description; ch.Category = def.Group;
                        ch.Enabled = true; ch.FilterJson = filterJson; ch.ScheduleStrategy = def.Strategy;
                        ch.RotationJson = def.RotationJson;
                        ch.SeasonStartMonth = def.SeasonStartMonth; ch.SeasonStartDay = def.SeasonStartDay;
                        ch.SeasonEndMonth = def.SeasonEndMonth; ch.SeasonEndDay = def.SeasonEndDay;
                        if (filterChanged && Apply)
                        {
                            var future = await db.ChannelScheduleItems
                                .Where(i => i.ChannelId == ch.Id && i.StartUtc > now).ToListAsync();
                            if (future.Count > 0) db.ChannelScheduleItems.RemoveRange(future);
                        }
                        updated++;
                    }
                    else unchanged++;
                }
                else
                {
                    var ch2 = new Channel
                    {
                        CatalogKey = def.Key, Name = def.Name, Description = def.Description, Category = def.Group,
                        SortOrder = sort, Enabled = true, FilterJson = filterJson,
                        ShuffleMode = "SeededShuffle", ScheduleStrategy = def.Strategy, RotationJson = def.RotationJson,
                        SeasonStartMonth = def.SeasonStartMonth, SeasonStartDay = def.SeasonStartDay,
                        SeasonEndMonth = def.SeasonEndMonth, SeasonEndDay = def.SeasonEndDay,
                        Seed = StableSeed(def.Key), AnchorUtc = now,
                    };
                    if (Apply) db.Channels.Add(ch2);
                    created++;
                }
            }

            var codeKeys = ChannelCatalog.All.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in existing.Where(c => c.CatalogKey != null && !codeKeys.Contains(c.CatalogKey!) && c.Enabled))
            {
                c.Enabled = false; disabled++;
                console.Output.WriteLine($"- disabling removed catalog channel: {c.Name}");
            }

            if (Apply && db.ChangeTracker.HasChanges())
                await db.SaveChangesAsync();

            if (warnings.Count > 0)
            {
                console.Output.WriteLine($"\n{warnings.Count} unresolved reference(s):");
                foreach (var w in warnings.Distinct()) console.Output.WriteLine(w);
            }

            console.Output.WriteLine($"\n{(Apply ? "APPLIED" : "DRY-RUN")}: +{created} created, {updated} updated, {unchanged} unchanged, {disabled} disabled  (catalog total {ChannelCatalog.All.Count})");

            if (PoolReport)
            {
                console.Output.WriteLine("\nPool sizes (eligible streamable titles per channel):");
                using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 200_000 });
                var svc = new ChannelScheduleService(db, cache, NullLogger<ChannelScheduleService>.Instance);
                int thin = 0;
                foreach (var def in ChannelCatalog.All)
                {
                    var probe = new Channel { Id = 0, FilterJson = resolvedJson[def.Key] };
                    int n;
                    try { n = (await svc.GetEligibleAsync(probe)).Items.Count; }
                    catch (Exception ex) { console.Output.WriteLine($"  !! {def.Key}: {ex.GetType().Name}: {ex.Message}"); continue; }
                    var flag = n == 0 ? "  <<< EMPTY" : n < 8 ? "  <<< THIN" : "";
                    if (n < 8) thin++;
                    console.Output.WriteLine($"  {n,6}  {def.Name}{flag}");
                }
                console.Output.WriteLine($"\n{thin} channel(s) under 8 titles.");
            }
        }
    }
}
