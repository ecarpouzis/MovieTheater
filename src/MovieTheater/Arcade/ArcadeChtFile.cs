using System;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Parser for a libretro cheat file (<c>libretro-database/cht/&lt;system&gt;/&lt;rom&gt;.cht</c>). Kept free of
    /// EF/CliFx so it can be unit-tested from source. Format:
    ///
    /// <code>
    /// cheats = 941
    ///
    /// cheat0_desc = "Infinite Lives"
    /// cheat0_code = "810C0A90 2409+810C0A92 0000"
    /// cheat0_enable = false
    /// </code>
    ///
    /// The declared <c>cheats = N</c> header is ignored: real files disagree with their own count, and the
    /// indices are what the entries are keyed by anyway.
    /// </summary>
    public static class ArcadeChtFile
    {
        /// <summary>One cheat: its index in the source file, its description, and the code string.
        /// A record CLASS, not a record struct — MovieTheater.csproj is pinned to C# 9.</summary>
        public sealed record Entry(int Ordinal, string Name, string Code);

        /// <summary>Longest code we will store (the DB column is nvarchar(4000)).</summary>
        public const int MaxCodeLength = 4000;

        /// <summary>RetroArch writes this as a cheat's "code" when the entry is a heading, not a cheat.</summary>
        private const string FolderMarker = "folder";

        /// <summary>Parse into ordered entries.
        ///
        /// <para><paramref name="withoutCode"/> counts entries that declare a description but no
        /// <c>_code</c>. Those are RetroArch's OWN memory-scanner cheats (address/value/type triples it
        /// pokes through the core's memory map) — we hand codes to <c>retro_cheat_set</c> and have no
        /// scanner, so importing them would create toggles that do nothing. They are counted, not stored.</para>
        ///
        /// <para>An over-long code is dropped rather than truncated: half a code pokes the wrong addresses.</para></summary>
        public static List<Entry> Parse(string text, out int withoutCode)
        {
            var descs = new Dictionary<int, string>();
            var codes = new Dictionary<int, string>();

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || !line.StartsWith("cheat", StringComparison.OrdinalIgnoreCase)) continue;

                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim().Trim('"');

                int us = key.IndexOf('_');
                // "cheats = 941" has no '_'; "cheat12_desc" has the index between "cheat" and '_'.
                if (us < 5 || !int.TryParse(key[5..us], out int idx)) continue;

                var field = key[(us + 1)..];
                if (field.Equals("desc", StringComparison.OrdinalIgnoreCase)) descs[idx] = val;
                else if (field.Equals("code", StringComparison.OrdinalIgnoreCase)) codes[idx] = val;
            }

            withoutCode = descs.Keys.Count(i => !codes.TryGetValue(i, out var c) || c.Length == 0);

            var result = new List<Entry>();
            foreach (var idx in codes.Keys.OrderBy(i => i))
            {
                var code = codes[idx];
                if (code.Length == 0 || code.Length > MaxCodeLength) continue;
                // "folder" is RetroArch's own marker for a heading row inside a cheat file ("Codes Are For
                // Proper Version", "NOTE: Read Description"), not a code. Cores reject it — melonDS DS
                // regex-checks its input and warns — so importing it puts a toggle in the picker that can
                // never do anything, which is the one thing this whole subsystem exists to prevent.
                if (code.Equals(FolderMarker, StringComparison.OrdinalIgnoreCase)) continue;
                var name = descs.TryGetValue(idx, out var d) && d.Length > 0 ? d : $"Cheat {idx + 1}";
                result.Add(new Entry(idx, name, code));
            }
            return result;
        }
    }
}
