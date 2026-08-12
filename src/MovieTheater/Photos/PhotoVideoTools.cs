using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MovieTheater.Photos
{
    /// <summary>
    /// What one video file turned out to be (docs/photos-plan.md §2.3, §2.5 phase 2: "videos via
    /// ffprobe"). Every field is nullable because every field is absent from some real container, and
    /// a missing duration is a fact to record rather than a reason to fail a row.
    /// </summary>
    public sealed class PhotoVideoInfo
    {
        public double? DurationSec { get; set; }

        /// <summary>DISPLAY dimensions — the rotation metadata is already applied, so the justified grid
        /// lays a portrait phone clip out portrait (the same contract the photo lane's EXIF orientation
        /// handling keeps, §2.2).</summary>
        public int? Width { get; set; }

        public int? Height { get; set; }

        /// <summary>The container's <c>creation_time</c>, a TRUE UTC instant (§2.7's second such source
        /// after GPS). Null when absent or nonsensical — see <see cref="FfmpegVideoTools"/>.</summary>
        public DateTime? CreationTimeUtc { get; set; }

        /// <summary>The readout, in the two-level shape <c>PhotoAsset.RawMetadataJson</c> already
        /// carries for EXIF, so the lightbox's info panel renders it with no second parser.</summary>
        public Dictionary<string, Dictionary<string, string>> Sections { get; } =
            new Dictionary<string, Dictionary<string, string>>();
    }

    /// <summary>
    /// The external-binary seam for video work.
    ///
    /// <para>It exists so the pipeline can be exercised with no ffmpeg on the machine and with no NAS
    /// in sight — the same reason <see cref="Services.IImmichApi"/> and
    /// <see cref="IPhotoJellyfinSource"/> exist. A test can hand the pass a probe that answers from a
    /// table, which is also the only honest way to assert what the pass DOES with a two-hour duration
    /// or a 1904 timestamp.</para>
    /// </summary>
    public interface IPhotoVideoTools
    {
        /// <summary>Whether this host can do video work at all. False degrades the pass to "say so and
        /// change nothing", never to a failure.</summary>
        bool Available { get; }

        /// <summary>Reads one file. Returns null when the binary failed, timed out, or produced
        /// something unparseable — all of which are the same thing to the caller: no answer.</summary>
        PhotoVideoInfo? Probe(string fullPath);

        /// <summary>Writes ONE frame from <paramref name="fullPath"/> at <paramref name="seconds"/> into
        /// <paramref name="destinationFile"/>. Returns false rather than throwing; the caller records
        /// the failure on the row and moves on.</summary>
        bool TryGrabFrame(string fullPath, double seconds, string destinationFile);
    }

    /// <summary>
    /// <see cref="IPhotoVideoTools"/> over real <c>ffprobe</c>/<c>ffmpeg</c> binaries.
    ///
    /// <para><b>Read-only, bounded, and never trusted.</b> Both binaries are invoked with the source
    /// file as an input only — no <c>-i</c> output path ever lands under the collection root, and the
    /// poster frame is written into the derivative cache (§6). Each invocation gets a hard runtime
    /// ceiling and is KILLED (with its children) when it passes it, because an ffmpeg that has wedged
    /// on a corrupt file would otherwise hold a bulk pass open indefinitely. Their stdout is a string
    /// from a program: it is size-capped, parsed inside a try, and every number goes through
    /// <c>TryParse</c> with the invariant culture before it becomes a column.</para>
    ///
    /// <para><b>Nonsensical timestamps are dropped, not stored.</b> QuickTime's epoch is 1904 and an
    /// unset field frequently surfaces as exactly that; so does a camera whose clock was never set.
    /// Writing those would put a wall of 1904 photographs at the bottom of a family timeline while
    /// wearing the authority of a real date — the same failure §2.7's undated shelf exists to prevent.</para>
    /// </summary>
    public sealed class FfmpegVideoTools : IPhotoVideoTools
    {
        private readonly string? ffprobePath;
        private readonly string? ffmpegPath;
        private readonly TimeSpan timeout;
        private readonly Action<string>? log;

        /// <summary>Per-invocation ceiling. Generous for a probe of a large container over SMB, short
        /// enough that a wedged binary cannot outlive a batch.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Most a binary may print before we stop reading it. An ffprobe JSON for a normal file
        /// is kilobytes; a megabyte means something has gone wrong and the rest is not worth the memory.</summary>
        private const int MaxOutputChars = 4 * 1024 * 1024;

        public FfmpegVideoTools(string? ffprobePath, string? ffmpegPath, TimeSpan? timeout = null, Action<string>? log = null)
        {
            this.ffprobePath = string.IsNullOrWhiteSpace(ffprobePath) ? null : ffprobePath;
            this.ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? null : ffmpegPath;
            this.timeout = timeout ?? DefaultTimeout;
            this.log = log;
        }

        public bool Available => ffprobePath != null;

        /// <summary>Whether poster frames can be written too. A host with ffprobe and no ffmpeg still
        /// gets durations and dimensions — the two are separate capabilities and the pass says which
        /// it had.</summary>
        public bool CanGrabFrames => ffmpegPath != null;

        public PhotoVideoInfo? Probe(string fullPath)
        {
            if (ffprobePath == null) return null;

            var arguments = new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-print_format", "json",
                "-show_format", "-show_streams",
                // "--" then the path: a filename beginning with a dash is a real thing in a family
                // tree and must never be read as an option.
                "--", fullPath,
            };

            var run = Run(ffprobePath, arguments);
            if (!run.Ok || run.StandardOutput.Length == 0) return null;
            return ParseProbeJson(run.StandardOutput);
        }

        public bool TryGrabFrame(string fullPath, double seconds, string destinationFile)
        {
            if (ffmpegPath == null) return false;
            var directory = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);

            // -ss BEFORE -i is the fast seek (keyframe-accurate, no decode of everything before it) —
            // the difference between a poster grab costing milliseconds and costing the whole file.
            // -an/-sn: audio and subtitles are decoded for nothing otherwise.
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            };
            if (seconds > 0.05)
            {
                arguments.Add("-ss");
                arguments.Add(seconds.ToString("0.###", CultureInfo.InvariantCulture));
            }
            arguments.Add("-i");
            arguments.Add(fullPath);
            arguments.AddRange(new[] { "-an", "-sn", "-frames:v", "1", "-f", "image2", destinationFile });

            var run = Run(ffmpegPath, arguments.ToArray());
            // Exit code alone is not proof: ffmpeg can return 0 having written nothing when the seek
            // landed past the end. The FILE is the evidence.
            return run.Ok && File.Exists(destinationFile) && new FileInfo(destinationFile).Length > 0;
        }

        // ── Parsing (never trusts the binary's stdout) ───────────────────────────────────────────

        /// <summary>
        /// Turns ffprobe's JSON into <see cref="PhotoVideoInfo"/>. Public and static so the golden
        /// outputs of real files can be asserted against it without a binary on the machine — the
        /// parsing is where a wrong answer would be silent, so it is the part that gets tested directly.
        /// </summary>
        public static PhotoVideoInfo? ParseProbeJson(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                var info = new PhotoVideoInfo();

                if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object)
                {
                    info.Sections["ffprobe format"] = Flatten(format);
                    info.DurationSec = Seconds(Text(format, "duration"));
                    info.CreationTimeUtc = Timestamp(Tag(format, "creation_time"));
                }

                if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    JsonElement? video = null;
                    foreach (var stream in streams.EnumerateArray())
                    {
                        if (stream.ValueKind != JsonValueKind.Object) continue;
                        var type = Text(stream, "codec_type") ?? "stream";
                        info.Sections[$"ffprobe {type} {index++}"] = Flatten(stream);
                        if (video == null && string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
                            video = stream;
                    }

                    if (video is JsonElement v)
                    {
                        info.Width = Integer(Text(v, "width"));
                        info.Height = Integer(Text(v, "height"));
                        info.DurationSec ??= Seconds(Text(v, "duration"));
                        info.CreationTimeUtc ??= Timestamp(Tag(v, "creation_time"));

                        // A phone records landscape and writes a rotation tag; the DISPLAY dimensions
                        // are what the grid lays out from, so swap here rather than making every
                        // consumer know about side_data (§2.2's "dimensions are display dimensions").
                        if (IsQuarterTurn(Rotation(v)) && info.Width != null && info.Height != null)
                            (info.Width, info.Height) = (info.Height, info.Width);
                    }
                }

                return info;
            }
            catch (JsonException)
            {
                // Output that is not JSON is the binary failing in a way it did not signal. No answer.
                return null;
            }
        }

        private static Dictionary<string, string> Flatten(JsonElement element)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    // One level of nesting (ffprobe's `tags`) is flattened with a prefix rather than
                    // dropped: creation_time and the phone's make/model live there.
                    foreach (var nested in property.Value.EnumerateObject())
                        Put(map, property.Name + "." + nested.Name, nested.Value);
                    continue;
                }
                if (property.Value.ValueKind == JsonValueKind.Array) continue;
                Put(map, property.Name, property.Value);
            }
            return map;
        }

        private static void Put(Dictionary<string, string> map, string key, JsonElement value)
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
            if (text == null) return;
            // A value from an external program goes into a column: bound it.
            map[Truncate(key, 128)] = Truncate(text, 512);
        }

        private static string Truncate(string value, int max) => value.Length <= max ? value : value.Substring(0, max);

        private static string? Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null,
                }
                : null;

        private static string? Tag(JsonElement element, string tag) =>
            element.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
                ? Text(tags, tag)
                : null;

        /// <summary>A duration in seconds, or null. Rejects negatives, NaN and anything longer than a
        /// week: those are parse artefacts, and a 10-year "duration" would break every UI that formats
        /// one.</summary>
        private static double? Seconds(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return null;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return null;
            if (seconds <= 0 || seconds > 7 * 24 * 3600) return null;
            return Math.Round(seconds, 3);
        }

        private static int? Integer(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0 && parsed <= 100_000
                ? parsed : null;

        /// <summary>
        /// The container's creation time as a UTC instant, or null.
        ///
        /// <para>Bounded on both ends deliberately. QuickTime's epoch is 1904-01-01 and an unset field
        /// routinely surfaces as exactly that; a camera with a dead clock produces 1970. Neither is a
        /// date this family took a video on, and storing them would fill the timeline's oldest end with
        /// confident nonsense. A future date is equally impossible and equally a clock fault.</para>
        /// </summary>
        private static DateTime? Timestamp(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                return null;
            var utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            if (utc.Year < 1990 || utc > DateTime.UtcNow.AddDays(1)) return null;
            return utc;
        }

        private static int Rotation(JsonElement stream)
        {
            // Two places carry it depending on the ffmpeg version: the stream's own `tags.rotate` and
            // the displaymatrix side-data's `rotation`. Only the tag survives Flatten, so read the
            // element directly here.
            var tag = Tag(stream, "rotate");
            if (tag != null && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromTag))
                return fromTag;

            if (stream.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var side in list.EnumerateArray())
                {
                    if (side.ValueKind != JsonValueKind.Object) continue;
                    var rotation = Text(side, "rotation");
                    if (rotation != null
                        && double.TryParse(rotation, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        return (int)Math.Round(value);
                }
            return 0;
        }

        private static bool IsQuarterTurn(int degrees)
        {
            var normalized = ((degrees % 360) + 360) % 360;
            return normalized == 90 || normalized == 270;
        }

        // ── Process running (bounded, killed on timeout) ─────────────────────────────────────────

        private readonly struct RunResult
        {
            public RunResult(bool ok, string standardOutput, string standardError)
            {
                Ok = ok;
                StandardOutput = standardOutput;
                StandardError = standardError;
            }

            public bool Ok { get; }
            public string StandardOutput { get; }
            public string StandardError { get; }
        }

        /// <summary>
        /// Runs a binary to completion or to the timeout, whichever comes first.
        ///
        /// <para>Output is drained on background handlers rather than read synchronously after the wait:
        /// a child whose pipe buffer fills blocks forever, and the "just call WaitForExit then read" shape
        /// is the classic way to deadlock exactly this. On timeout the process TREE is killed — ffmpeg
        /// spawns nothing today, but a killed parent leaving a live child is how a bulk pass ends up
        /// with a hundred orphans.</para>
        /// </summary>
        private RunResult Run(string executable, string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            var output = new StringBuilder();
            var error = new StringBuilder();
            try
            {
                using var process = new Process { StartInfo = start };
                process.OutputDataReceived += (_, e) => Append(output, e.Data);
                process.ErrorDataReceived += (_, e) => Append(error, e.Data);
                if (!process.Start()) return new RunResult(false, "", "could not start " + executable);

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                // Nothing is ever fed in; closing it means a binary that decides to prompt gets EOF
                // instead of waiting for a human who is not there.
                process.StandardInput.Close();

                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                    log?.Invoke($"  ! {Path.GetFileName(executable)} exceeded {timeout.TotalSeconds:0}s and was killed");
                    return new RunResult(false, "", $"timed out after {timeout.TotalSeconds:0}s");
                }

                // Flushes the async readers; the process has exited so this cannot block.
                process.WaitForExit();
                return new RunResult(process.ExitCode == 0, output.ToString(), error.ToString());
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException || e is IOException)
            {
                // A missing or unusable binary is a configuration fact, not a crash.
                return new RunResult(false, "", e.Message);
            }
        }

        private static void Append(StringBuilder builder, string? line)
        {
            if (line == null) return;
            lock (builder)
            {
                if (builder.Length >= MaxOutputChars) return;
                builder.Append(line).Append('\n');
            }
        }
    }
}
