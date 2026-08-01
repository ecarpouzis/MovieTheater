using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Reads a GameCube/Wii disc image's <b>game id</b> and revision by shelling out to <c>DolphinTool</c>.
    ///
    /// <para>The id (<c>GFZE01</c>) — not the filename — is what selects a game's cheats, because that is what
    /// Dolphin itself keys its INIs by. Going through the id means GameCube/Wii cheats never rely on the
    /// filename matching that every other system needs, and therefore carry <b>no cross-region risk</b>: the
    /// region is the 4th character of the id, read out of the disc header.</para>
    ///
    /// <para>Shelling out rather than parsing the header ourselves is deliberate. The id sits at offset 0 of
    /// the raw disc, but our library is stored as <c>.gcz</c> and <c>.rvz</c> — compressed container formats
    /// whose first block has to be decompressed before that offset exists. DolphinTool already implements
    /// every container Dolphin can boot (which is exactly the set we can run), and it reads only the header:
    /// ~0.1 s per local file, ~0.3 s over the NAS.</para>
    /// </summary>
    public static class DolphinDiscId
    {
        public sealed record DiscHeader(string GameId, int Revision, string? Country, string? InternalName);

        /// <summary>Reads one image's header, or null if the tool fails, times out, or the file is not a disc
        /// image it understands. A null is always a SKIP — never a guess, because a wrong id would hand a game
        /// another game's memory pokes.</summary>
        public static async Task<DiscHeader?> ReadAsync(string dolphinTool, string imagePath, CancellationToken ct = default)
        {
            if (!File.Exists(imagePath)) return null;

            var psi = new ProcessStartInfo(dolphinTool)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("header");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(imagePath);
            psi.ArgumentList.Add("-j");

            try
            {
                using var p = Process.Start(psi);
                if (p == null) return null;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));

                var stdout = await p.StandardOutput.ReadToEndAsync();
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { try { p.Kill(true); } catch { /* already gone */ } return null; }

                if (p.ExitCode != 0 || stdout.Length == 0) return null;
                return FromJson(stdout);
            }
            catch (Exception)
            {
                // A missing/unrunnable tool, a locked file, a container it can't open: all the same answer.
                return null;
            }
        }

        /// <summary>Parses DolphinTool's <c>--json</c> header output. Separated out so it can be unit-tested
        /// without the executable.</summary>
        internal static DiscHeader? FromJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("game_id", out var idEl)) return null;
                var id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id)) return null;

                int revision = 0;
                if (root.TryGetProperty("revision", out var revEl))
                {
                    if (revEl.ValueKind == JsonValueKind.Number) revision = revEl.GetInt32();
                    else if (revEl.ValueKind == JsonValueKind.String
                             && int.TryParse(revEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                        revision = r;
                }

                return new DiscHeader(
                    id.Trim(),
                    revision,
                    root.TryGetProperty("country", out var c) ? c.GetString() : null,
                    root.TryGetProperty("internal_name", out var n) ? n.GetString() : null);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
