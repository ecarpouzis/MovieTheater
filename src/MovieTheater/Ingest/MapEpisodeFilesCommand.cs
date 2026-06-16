using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieTheater.Console;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Ingest
{
    /// <summary>
    /// Applies the episode→file matches from <c>data/_episode_filemap.csv</c>: for each matched file, ensure
    /// its <see cref="Episode"/> has a <see cref="Playable"/> (create if missing) and add a
    /// <see cref="MediaFile"/> (Role=Primary). The match strategy (se/title/absolute/…) is stored in
    /// <see cref="MediaFile.Label"/> as <c>"match:&lt;strategy&gt;"</c> so the review tool can surface how each
    /// file was matched. Idempotent by file path.
    /// </summary>
    [Command("map-episode-files", Description = "Create Playable + MediaFile for each matched episode file (from _episode_filemap.csv).")]
    public class MapEpisodeFilesCommand : BasicDICommand, ICommand
    {
        [CommandOption("csv", Description = "Episode file-map CSV (default: data/_episode_filemap.csv).")]
        public string CsvPath { get; set; } = Path.Combine("data", "_episode_filemap.csv");

        [CommandOption("dry-run", Description = "Report what would be written, without writing.")]
        public bool DryRun { get; set; }

        private readonly IDbContextFactory<MovieDb> dbFactory;
        private readonly ILogger<MapEpisodeFilesCommand> logger;

        public MapEpisodeFilesCommand(MovieTheaterConfiguration config) : base(config)
        {
            dbFactory = GetRequiredService<IDbContextFactory<MovieDb>>();
            logger = GetRequiredService<ILogger<MapEpisodeFilesCommand>>();
        }

        private static string NormPath(string p) => (p ?? "").Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        public ValueTask ExecuteAsync(IConsole console) => new ValueTask(RunAsync(console));

        private async Task RunAsync(IConsole console)
        {
            var path = Path.GetFullPath(CsvPath);
            if (!File.Exists(path)) { console.Error.WriteLine($"CSV not found: {path}"); return; }
            var rows = ParseCsv(path);
            console.Output.WriteLine($"matched episode files in CSV: {rows.Count}");

            await using var db = await dbFactory.CreateDbContextAsync();
            var eps = await db.Episodes.ToListAsync();
            var epIndex = new Dictionary<(int, int, int), Episode>();
            foreach (var e in eps) epIndex[(e.SeriesMovieId, e.SeasonNumber, e.EpisodeNumber)] = e;
            var existingPaths = (await db.MediaFiles.Select(f => f.Path).ToListAsync()).Select(NormPath).ToHashSet();

            int playables = 0, mediafiles = 0, missingEp = 0, dupPath = 0, pending = 0;
            foreach (var r in rows)
            {
                if (!int.TryParse(r.Get("SeriesMovieId"), out var sid) ||
                    !int.TryParse(r.Get("Season"), out var sn) ||
                    !int.TryParse(r.Get("Episode"), out var en)) continue;
                var fpath = r.Get("path");
                if (string.IsNullOrWhiteSpace(fpath)) continue;

                if (!epIndex.TryGetValue((sid, sn, en), out var ep)) { missingEp++; continue; }
                if (existingPaths.Contains(NormPath(fpath))) { dupPath++; continue; }
                existingPaths.Add(NormPath(fpath));

                var label = "match:" + (r.Get("strategy") ?? "?");
                MediaFile mf;
                if (ep.PlayableId != null)
                {
                    mf = new MediaFile { PlayableId = ep.PlayableId.Value, Path = fpath, Role = MovieFileRole.Primary, Label = label };
                }
                else
                {
                    if (ep.Playable == null) { ep.Playable = new Playable { Kind = PlayableKind.Episode }; playables++; }
                    mf = new MediaFile { Playable = ep.Playable, Path = fpath, Role = MovieFileRole.Primary, Label = label };
                }
                if (!DryRun) db.MediaFiles.Add(mf);
                mediafiles++;
                if (!DryRun && ++pending >= 500) { await db.SaveChangesAsync(); pending = 0; }
            }
            if (!DryRun && pending > 0) await db.SaveChangesAsync();

            console.Output.WriteLine($"{(DryRun ? "DRY RUN — " : "")}episode Playables created: {playables}; MediaFiles added: {mediafiles}; "
                + $"skipped (already have file): {dupPath}; episode row not found: {missingEp}");
            logger.LogInformation("map-episode-files: +{Playables} playables, +{MediaFiles} mediafiles ({DryRun})", playables, mediafiles, DryRun);
        }

        private sealed class Row
        {
            private readonly Dictionary<string, string> f;
            public Row(Dictionary<string, string> f) => this.f = f;
            public string Get(string c) => f.TryGetValue(c, out var v) ? v : null;
        }

        private static List<Row> ParseCsv(string path)
        {
            var recs = ParseRecords(path);
            var result = new List<Row>();
            if (recs.Count == 0) return result;
            var header = recs[0];
            for (int i = 1; i < recs.Count; i++)
            {
                var fr = recs[i];
                if (fr.Count == 1 && fr[0].Length == 0) continue;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < header.Count && c < fr.Count; c++) d[header[c]] = fr[c];
                result.Add(new Row(d));
            }
            return result;
        }

        private static List<List<string>> ParseRecords(string path)
        {
            var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
            var records = new List<List<string>>();
            var field = new StringBuilder();
            var record = new List<string>();
            bool inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (inQuotes)
                {
                    if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else inQuotes = false; }
                    else field.Append(ch);
                }
                else
                {
                    switch (ch)
                    {
                        case '"': inQuotes = true; break;
                        case ',': record.Add(field.ToString()); field.Clear(); break;
                        case '\r': break;
                        case '\n': record.Add(field.ToString()); field.Clear(); records.Add(record); record = new List<string>(); break;
                        default: field.Append(ch); break;
                    }
                }
            }
            if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString()); records.Add(record); }
            return records;
        }
    }
}
