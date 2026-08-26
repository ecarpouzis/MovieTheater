using System.Text.Json;
using System.Text.Json.Serialization;

namespace MovieTheater.Books.Services
{
    /// <summary>
    /// The settings an ADMIN may change at runtime, kept in an overlay file (<c>data/books/books.settings.json</c>)
    /// that sits on top of the host's own configuration.
    ///
    /// <para><b>The allow-list is the whole design.</b> Only the keys named here can be written: the ComicVine
    /// API key, the two image-quality dials and the archive-cache budget. PATHS and SECRETS other than that key
    /// are deliberately NOT settable — a config endpoint that could re-point `DbPath` or `MediaTokenSecret`
    /// would let an admin account move the database or forge media tokens, which is a different privilege from
    /// "change the thumbnail quality". An unknown key is REJECTED, not ignored, so a typo is visible.</para>
    ///
    /// <para>The file is written atomically (temp + replace) so a torn write cannot leave the host without
    /// settings, and it is re-read on every GET so two hosts never disagree about what it says.</para>
    /// </summary>
    public sealed class BooksSettingsOverlay
    {
        /// <summary>The keys an admin may set, and what each accepts.</summary>
        public enum Kind { Secret, Int }

        public sealed record Key(string Name, Kind Kind, int Min = 0, int Max = 0, string Description = "");

        public static readonly IReadOnlyList<Key> AllowedKeys = new[]
        {
            new Key("ComicVineApiKey", Kind.Secret, Description: "The ComicVine API key. Plain configuration — there is no per-user key vault."),
            new Key("ThumbnailQuality", Kind.Int, 40, 100, "WebP quality for a generated cover thumbnail."),
            new Key("PageJpegQuality", Kind.Int, 40, 100, "JPEG quality for a scaled page on the wire."),
            new Key("ArchiveCacheGb", Kind.Int, 0, 4096, "Budget for the whole-archive copy cache, in GB. 0 turns it off."),
        };

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly string? path;
        private readonly object gate = new();

        public BooksSettingsOverlay(string? path) => this.path = path;

        public string? Path => path;
        public bool Configured => path != null;

        /// <summary>Everything the overlay holds. A SECRET is reported as present/absent, never echoed.</summary>
        public Dictionary<string, object?> Read()
        {
            var stored = ReadRaw();
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in AllowedKeys)
            {
                stored.TryGetValue(key.Name, out var value);
                result[key.Name] = key.Kind == Kind.Secret
                    ? (object?)(value is string s && s.Length > 0 ? "(set)" : null)
                    : value;
            }
            return result;
        }

        /// <summary>The raw value of one key — what the runtime reads. Never served over HTTP for a secret.</summary>
        public string? Value(string name)
        {
            ReadRaw().TryGetValue(name, out var v);
            return v?.ToString();
        }

        /// <summary>
        /// Write one or more keys. An unknown key or an out-of-range number is refused outright; a null clears
        /// the key back to whatever the host's own configuration says.
        /// </summary>
        public Dictionary<string, object?> Write(IReadOnlyDictionary<string, object?> updates)
        {
            if (path == null) throw new InvalidOperationException("No settings overlay path is configured on this host.");

            lock (gate)
            {
                var stored = ReadRaw();
                foreach (var (name, value) in updates)
                {
                    var key = AllowedKeys.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.Ordinal))
                        ?? throw new ArgumentException($"'{name}' is not an admin-settable key. Settable: {string.Join(", ", AllowedKeys.Select(k => k.Name))}.");
                    if (value == null) { stored.Remove(name); continue; }

                    if (key.Kind == Kind.Int)
                    {
                        if (!int.TryParse(value.ToString(), out var n))
                            throw new ArgumentException($"'{name}' takes a number.");
                        if (n < key.Min || n > key.Max)
                            throw new ArgumentException($"'{name}' must be between {key.Min} and {key.Max}.");
                        stored[name] = n;
                    }
                    else
                    {
                        var text = value.ToString() ?? "";
                        if (text.Trim().Length == 0) stored.Remove(name);
                        else stored[name] = text.Trim();
                    }
                }

                var directory = System.IO.Path.GetDirectoryName(path);
                if (directory != null) Directory.CreateDirectory(directory);
                // Atomic: a torn write must never leave the host with half a settings file.
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(stored, Json));
                File.Move(temp, path, overwrite: true);
            }
            return Read();
        }

        private Dictionary<string, object?> ReadRaw()
        {
            if (path == null || !File.Exists(path)) return new Dictionary<string, object?>(StringComparer.Ordinal);
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (k, v) in parsed ?? new Dictionary<string, JsonElement>())
                    result[k] = v.ValueKind switch
                    {
                        JsonValueKind.String => v.GetString(),
                        JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null,
                    };
                return result;
            }
            catch (JsonException)
            {
                // A corrupt overlay degrades to "no overlay" rather than taking the host down at startup.
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }
        }
    }
}
