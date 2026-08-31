using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using MovieTheater.Console;
using MovieTheater.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;

namespace MovieTheater.Web
{
    /// <summary>
    /// Re-encodes the thumbnails already on the images mount from PNG to WebP (2026-08-31).
    ///
    /// <para><b>Why there is a job at all.</b> Changing the recipe only changes thumbnails written from
    /// now on, and almost nothing regenerates: a poster thumb is written once and
    /// <c>HasImage</c> is authoritative, so the library would keep serving 78–125 KB PNGs forever. The
    /// measured difference is ~10x (a 300 px cover: 125 KB PNG, 12.9 KB WebP q82), and the music grid
    /// asks for 22 covers at once — 2.75 MB a screen, which is what makes it feel slow.</para>
    ///
    /// <para><b>What it will and will not touch.</b> Two populations, and between them that is every
    /// thumbnail the site serves:</para>
    /// <list type="bullet">
    /// <item><c>*_s.png</c> at the root — the universal thumbnail suffix (<c>{id}_s.png</c>,
    /// <c>{bucket}_{id}_s.png</c>, <c>music_{id}_s.png</c>).</item>
    /// <item>everything under <c>arcade/</c> — box art is written ONLY by
    /// <c>ArcadeBoxArt.Thumbnail</c>, so every file in that tree is already a thumbnail. It has no
    /// full-size counterpart, which is exactly why it has to be included.</item>
    /// </list>
    /// <para>The full-size originals at the root (<c>{id}.png</c>, no <c>_s</c>) are NEVER opened: they
    /// are what the detail views actually serve, and they are the source for any future re-encode. That
    /// is the whole reason a thumbnail can be lossy without costing anything — the quality that matters
    /// is still on disk, untouched.</para>
    ///
    /// <para><b>Bulk-job rules, all of them.</b> Dry run by default; <c>--apply</c> writes. Bounded by
    /// <c>--limit</c> files per run, resumable via <c>--after &lt;filename&gt;</c> — and the cursor is a
    /// filename because the walk is ORDERED by filename, so "everything after X" means the same thing on
    /// every run. Idempotent: a file that is already WebP is skipped, so a re-run is free and an
    /// interrupted run simply continues. Every chunk prints
    /// <c>{ processed, rewritten, skipped, remaining, nextCursor }</c>.</para>
    ///
    /// <para><b>Guards, because this overwrites real files.</b> A file is rewritten only if it decodes,
    /// re-encodes, decodes AGAIN at the same pixel size, and comes out SMALLER. Anything else is left
    /// exactly as it was and counted as a skip — a thumbnail that will not survive the round trip keeps
    /// the bytes it has. The write goes to a temp file in the same directory and is then moved over the
    /// original, so a crash mid-write cannot leave a truncated thumbnail behind.</para>
    /// </summary>
    [Command("thumbs-recode", Description = "Re-encode *_s.png thumbnails on the images mount to WebP (dry-run unless --apply).")]
    public class ThumbsRecodeCommand : BasicDICommand, ICommand
    {
        [CommandOption("dir", 'd', Description = "Images directory. Default: MoviePostersDir from config.")]
        public string? Dir { get; set; }

        [CommandOption("apply", Description = "Write changes. Omit for a dry run (default).")]
        public bool Apply { get; set; }

        [CommandOption("limit", Description = "Max FILES to process this run (default 500).")]
        public int Limit { get; set; } = 500;

        [CommandOption("after", Description = "Resume cursor: skip files whose name is ≤ this (from a prior run's nextCursor).")]
        public string? After { get; set; }

        [CommandOption("verbose", Description = "Print a line per file, not just the ones that changed.")]
        public bool Verbose { get; set; }

        private readonly MovieTheaterConfiguration config;

        public ThumbsRecodeCommand(MovieTheaterConfiguration config) : base(config) => this.config = config;

        public async ValueTask ExecuteAsync(IConsole console)
        {
            var w = console.Output;
            var dir = !string.IsNullOrWhiteSpace(Dir) ? Path.GetFullPath(Dir) : config.MoviePostersDir;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                w.WriteLine($"Images directory not found: {dir ?? "(unset — pass --dir or set MoviePostersDir)"}");
                return;
            }

            // Both populations, ordered — the same walk the service uses (ThumbRecoder), so a hand run
            // and the overnight pass can never disagree about what a cursor means.
            var all = ThumbRecoder.Candidates(dir);
            var pending = string.IsNullOrEmpty(After)
                ? all
                : all.Where(r => string.CompareOrdinal(r, After) > 0).ToList();

            var batch = pending.Take(Math.Max(1, Limit)).ToList();
            long before = 0, after = 0;
            int rewritten = 0, skipped = 0, failed = 0;
            string? cursor = null;

            foreach (var rel in batch)
            {
                cursor = rel;
                var outcome = await ThumbRecoder.RecodeAsync(
                    dir, rel, MovieTheater.Services.Poster.ImageShrinkService.ThumbnailQuality, Apply);
                if (outcome.Rewritten)
                {
                    rewritten++; before += outcome.Before; after += outcome.After;
                    if (Verbose || !Apply)
                        w.WriteLine($"  {(Apply ? "→" : "would")} {cursor}: {outcome.Before:N0} → {outcome.After:N0} B");
                }
                else if (outcome.Reason.StartsWith("already", StringComparison.Ordinal)
                         || outcome.Reason.StartsWith("webp not smaller", StringComparison.Ordinal))
                {
                    skipped++;
                    if (Verbose) w.WriteLine($"  = {cursor}: {outcome.Reason}");
                }
                else
                {
                    failed++;
                    w.WriteLine($"  ! {cursor}: {outcome.Reason}");
                }
            }

            var remaining = Math.Max(0, pending.Count - batch.Count);
            w.WriteLine(Apply ? "thumbs-recode: APPLIED" : "thumbs-recode: DRY RUN (pass --apply to write)");
            w.WriteLine($"  dir={dir} (root *_s.png + everything under arcade/)");
            w.WriteLine($"  processed={batch.Count} rewritten={rewritten} skipped={skipped} failed={failed}");
            w.WriteLine($"  bytes {before:N0} → {after:N0}" + (before > 0 ? $" ({100.0 * (before - after) / before:F1}% smaller)" : ""));
            w.WriteLine($"  remaining={remaining} nextCursor={cursor ?? After ?? "(none)"}");
            if (remaining > 0)
                w.WriteLine($"  re-run with --after {cursor} to continue.");
        }
    }
}
