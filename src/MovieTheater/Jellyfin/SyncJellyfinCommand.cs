using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Console;
using MovieTheater.Services;
using MovieTheater.Services.Jellyfin;

namespace MovieTheater.Jellyfin
{
    /// <summary>
    /// Matches Jellyfin's library against the DB's stored file paths and records the result in
    /// <see cref="MovieTheater.Db.MediaFile"/> (docs/streaming-plan.md §6). The matching/writing logic
    /// lives in <see cref="JellyfinSyncService"/> (shared with the admin "Sync from Jellyfin" button);
    /// this command runs it and prints the two-way diff. Re-runnable any time.
    /// </summary>
    [Command("sync-jellyfin", Description = "Match Jellyfin items to movie file paths and store ids + media details.")]
    public class SyncJellyfinCommand : BasicDICommand, ICommand
    {
        [CommandOption("dry-run", Description = "Match and report without writing to the database.")]
        public bool DryRun { get; set; }

        [CommandOption("samples", Description = "How many examples to print per report section.")]
        public int Samples { get; set; } = 15;

        private readonly JellyfinSyncService syncService;

        public SyncJellyfinCommand(MovieTheaterConfiguration config) : base(config)
        {
            syncService = GetRequiredService<JellyfinSyncService>();
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var cancel = console.RegisterCancellationHandler();
            var o = console.Output;

            var report = await syncService.RunAsync(DryRun, cancel);
            if (report.Aborted != null)
            {
                console.Error.WriteLine(report.Aborted + " Aborting.");
                return;
            }

            o.WriteLine($"Jellyfin: {report.ServerName} {report.Version}{(DryRun ? "   (dry-run)" : "")}");
            o.WriteLine($"Jellyfin media items (all types): {report.MovieItems}");
            // §2.3: the family photo library is structurally excluded. Printed every run so the
            // exclusion is visibly on, and so a misconfigured prefix is noticed here rather than by a
            // home video turning up in the movie grid.
            o.WriteLine(report.FamilyExclusionPrefixes.Count == 0
                ? "Family photo library exclusion: NOT configured (set PhotosLibraryDir) — photos-plan.md §2.3"
                : $"Family photo library excluded: {report.FamilyItemsExcluded} item(s) dropped under "
                  + string.Join(" | ", report.FamilyExclusionPrefixes));
            o.WriteLine($"DB movies with a file path: {report.MoviesWithPath}" +
                        (DryRun ? "" : $"   existing MediaFile rows: {report.ExistingFileRows}"));

            o.WriteLine("");
            o.WriteLine($"Movies matched by path: {report.MoviesMatched}/{report.MoviesTotal} " +
                        $"({100.0 * report.MoviesMatched / Math.Max(1, report.MoviesTotal):F1}%)" +
                        (DryRun ? "" : $" — rows created {report.Created}, updated {report.Updated}"));
            o.WriteLine($"Jellyfin candidate items for episode/part/misc matching: {report.EpVidItems}");
            o.WriteLine($"Episode/movie-part/misc files matched by path: {report.EpMatched}/{report.EpTotal}" +
                        (report.EpTotal == 0 ? "" : $" ({100.0 * report.EpMatched / report.EpTotal:F1}%)"));
            o.WriteLine($"Moved/renamed files re-pointed by (name+size): {report.Repointed.Count}" +
                        (report.SupersededOrphans > 0 ? $" ({report.SupersededOrphans} rescued from renamed-folder leftovers)" : "") +
                        (DryRun ? "   (dry-run — not written)" : ""));

            PrintSection(o, $"Moved files / renamed folders re-pointed ({report.Repointed.Count})", report.Repointed);
            PrintSection(o, $"Possible file renames — same size, name changed, review not applied ({report.PossibleRenames.Count})", report.PossibleRenames);
            PrintSection(o, $"DB titles with no Jellyfin item, even after move-detection ({report.MissingMovies.Count})" +
                            (DryRun ? "" : " — MissingSinceUtc stamped on existing rows"), report.MissingMovies);
            PrintSection(o, $"Jellyfin items the DB doesn't track ({report.Untracked.Count})", report.Untracked);
            PrintSection(o, $"IMDB-id fallback candidates — review, not written ({report.ImdbFallbacks.Count})", report.ImdbFallbacks);
            PrintSection(o, $"Jellyfin paths no mapping covers ({report.Untranslatable.Count})", report.Untranslatable);
            PrintSection(o, $"Duplicate Jellyfin items for one movie — earlier-listed mapping kept ({report.DuplicateItems.Count})", report.DuplicateItems);
            PrintSection(o, $"Duplicate DB file paths ({report.DuplicatePaths.Count})", report.DuplicatePaths);
        }

        private void PrintSection(ConsoleWriter o, string heading, IReadOnlyList<string> lines)
        {
            o.WriteLine("");
            o.WriteLine(heading);
            int shown = 0;
            foreach (var line in lines)
            {
                if (shown++ >= Samples) { o.WriteLine($"  … ({heading.Split('(')[0].Trim()}: more omitted, raise --samples to see)"); break; }
                o.WriteLine($"  {line}");
            }
            if (shown == 0) o.WriteLine("  (none)");
        }
    }
}
