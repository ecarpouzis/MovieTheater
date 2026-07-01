using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Channels
{
    /// <summary>
    /// Creates the default TV channels (streaming-plan.md §10 Phase 4): "Everything", a
    /// set of genre channels, a family-safe channel, and optionally an "Unseen by &lt;user&gt;"
    /// channel. Idempotent on channel Name — re-running adds only the missing ones.
    /// </summary>
    [Command("seed-channels", Description = "Create the default TV channels if they don't already exist.")]
    public class SeedChannelsCommand : BasicDICommand, ICommand
    {
        [CommandOption("unseen-user", Description = "Username to build an 'Unseen by <user>' channel for.")]
        public string? UnseenUser { get; set; }

        [CommandOption("reset", Description = "Delete all existing channels (and their schedules) first.")]
        public bool Reset { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;

        public SeedChannelsCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            using var db = await dbFactory.CreateDbContextAsync();

            if (Reset)
            {
                db.ChannelScheduleItems.RemoveRange(db.ChannelScheduleItems);
                db.Channels.RemoveRange(db.Channels);
                await db.SaveChangesAsync();
                console.Output.WriteLine("Removed existing channels and schedules.");
            }

            var genres = await db.Genres.ToDictionaryAsync(g => g.Name, g => g.Id, StringComparer.OrdinalIgnoreCase);
            int GenreId(string name) => genres.TryGetValue(name, out var id) ? id : -1;

            var anchor = DateTime.UtcNow;
            var seedRng = new Random();
            var defs = new List<(string Name, string? Desc, int Sort, ChannelFilter Filter)>
            {
                ("Everything", "The whole library on shuffle", 0, new ChannelFilter()),
                ("Family", "G through PG-13, all ages welcome", 1, new ChannelFilter { MaxMpaRatingId = 3 }),
                ("Action & Adventure", "Explosions and quests", 2, GenreFilter(GenreId("Action"), GenreId("Adventure"))),
                ("Comedy", "Laughs around the clock", 3, GenreFilter(GenreId("Comedy"))),
                ("Horror", "Late-night frights", 4, GenreFilter(GenreId("Horror"))),
                ("Sci-Fi & Fantasy", "Other worlds", 5, GenreFilter(GenreId("Sci-Fi"), GenreId("Fantasy"))),
                ("Drama", "Serious cinema", 6, GenreFilter(GenreId("Drama"))),
                ("Classics", "Everything before 1970", 7, new ChannelFilter { YearMax = 1969 }),
            };

            if (!string.IsNullOrWhiteSpace(UnseenUser))
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.Username == UnseenUser);
                if (user == null)
                {
                    console.Error.WriteLine($"User '{UnseenUser}' not found — skipping the unseen channel.");
                }
                else
                {
                    // A per-user "Unseen by X" channel is personal, so it lives on the "For You" shelf and
                    // is scoped to that user via OwnerUserId (without it the channel would leak to everyone).
                    var unseenName = $"Unseen by {user.Username}";
                    if (!await db.Channels.AnyAsync(c => c.Name == unseenName))
                    {
                        db.Channels.Add(new Channel
                        {
                            Name = unseenName,
                            Description = "Things you haven't watched yet",
                            SortOrder = 8,
                            Enabled = true,
                            FilterJson = new ChannelFilter { UnwatchedByUserId = user.UserID }.ToJson(),
                            Seed = seedRng.Next(1, int.MaxValue),
                            ShuffleMode = "SeededShuffle",
                            AnchorUtc = anchor,
                            Category = "For You",
                            OwnerUserId = user.UserID,
                        });
                        console.Output.WriteLine($"+ {unseenName}");
                    }
                }
            }

            var existing = await db.Channels.Select(c => c.Name).ToListAsync();
            var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

            int created = 0;
            foreach (var (name, desc, sort, filter) in defs)
            {
                if (existingSet.Contains(name))
                    continue;
                // Drop genre filters that didn't resolve to real genre ids.
                filter.GenreIds = filter.GenreIds.Where(id => id > 0).ToList();
                db.Channels.Add(new Channel
                {
                    Name = name,
                    Description = desc,
                    SortOrder = sort,
                    Enabled = true,
                    FilterJson = filter.ToJson(),
                    Seed = seedRng.Next(1, int.MaxValue),
                    ShuffleMode = "SeededShuffle",
                    AnchorUtc = anchor,
                });
                created++;
                console.Output.WriteLine($"+ {name}");
            }

            await db.SaveChangesAsync();
            console.Output.WriteLine($"Done. Created {created} channel(s); {existingSet.Count} already existed.");
        }

        private static ChannelFilter GenreFilter(params int[] genreIds) =>
            new() { GenreIds = genreIds.ToList(), GenreMode = "any" };
    }
}
