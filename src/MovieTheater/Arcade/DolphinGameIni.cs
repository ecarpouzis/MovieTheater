using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Reads the cheats out of Dolphin's own per-game settings INIs (<c>Sys/GameSettings/&lt;GAMEID&gt;.ini</c>),
    /// which is the ONLY way GameCube/Wii cheats can reach the emulator. See docs/arcade-cheats.md.
    ///
    /// <para><b>Why this exists instead of a cht folder.</b> Dolphin's <c>retro_cheat_set</c> is a real
    /// implementation (the probe says so), but it does not accept an arbitrary code the way every other core
    /// does. It only flips a cheat Dolphin <i>already loaded from its own INIs</i>, found by comparing the
    /// incoming string to a re-serialization of each loaded code
    /// (<c>enable_cheat_by_code</c> in <c>DolphinLibretro/Main.cpp</c>). Hand it a Gecko code Dolphin has
    /// never heard of and it matches nothing and returns — silently, like every other cheat failure in this
    /// subsystem. So the site's job is not to supply codes; it is to <i>mirror the ones the core already has</i>
    /// and hand back the exact bytes the comparison expects.</para>
    ///
    /// <para><b>The serialization is therefore a wire format, not a display choice.</b> One byte off and the
    /// cheat silently does nothing:</para>
    /// <list type="bullet">
    ///   <item><b>ActionReplay</b> — each op re-formatted as <c>{cmd_addr:X8} {value:X8}</c> (UPPERCASE, one
    ///     space) and '+'-joined. Not the INI's own text: Dolphin parses to integers and formats them back,
    ///     so a lowercase or oddly-spaced source line still has to be emitted canonically.</item>
    ///   <item><b>Gecko</b> — each line's <c>original_line</c> VERBATIM (post whitespace-strip and
    ///     comment-removal, which is how Dolphin's INI reader hands them over) and '+'-joined.</item>
    /// </list>
    ///
    /// <para>Verified against an oracle rather than by reading the source twice: the core itself writes a
    /// RetroArch <c>.cht</c> of every code it loaded (<c>generate_cht_from_ini</c>) using that same
    /// serialization, and this parser reproduces those files byte for byte (97/97 cheats over three games
    /// mixing both code kinds, 2026-07-31).</para>
    /// </summary>
    public static class DolphinGameIni
    {
        /// <summary>One cheat from a Dolphin game INI. <paramref name="Code"/> is the exact string
        /// <c>retro_cheat_set</c> must receive.</summary>
        public sealed record Cheat(string Name, string Code, string Kind);

        /// <summary>The INI chain Dolphin loads for a disc, in its own order
        /// (<c>ConfigLoaders::GetGameIniFilenames</c>): system letter, then the region-agnostic 3-char id,
        /// then the full id, then the revision-specific file. Every one that exists is layered on top of the
        /// last, so a game's real cheat list is the UNION — miss this and F-Zero GX gets whatever generic
        /// <c>GFZ.ini</c> holds instead of the four codes in <c>GFZE01.ini</c>.</summary>
        public static IEnumerable<string> IniChain(string gameId, int revision)
        {
            if (string.IsNullOrEmpty(gameId)) yield break;
            if (gameId.Length == 6)
            {
                yield return gameId[..1] + ".ini";
                yield return gameId[..3] + ".ini";
            }
            yield return gameId + ".ini";
            yield return $"{gameId}r{revision.ToString(CultureInfo.InvariantCulture)}.ini";
        }

        /// <summary>Parse the cheats out of one or more INI texts (pass the chain in load order; the
        /// concatenation is equivalent to Dolphin's layered <c>IniFile::Load(keep_current_data: true)</c>,
        /// which appends section lines rather than replacing them).</summary>
        /// <param name="skippedUnreproducible">ActionReplay codes containing a line we cannot re-serialize —
        /// in practice the ENCRYPTED form, which Dolphin decrypts at load. We would have to reimplement that
        /// decryption to predict the resulting ops, so such a code is dropped rather than offered as a toggle
        /// whose string will never match.</param>
        public static IReadOnlyList<Cheat> Parse(IEnumerable<string> iniTexts, out int skippedUnreproducible)
        {
            var joined = string.Join("\n", iniTexts);
            var ar = ParseActionReplay(SectionLines(joined, "ActionReplay"), out skippedUnreproducible);
            var gecko = ParseGecko(SectionLines(joined, "Gecko"));
            // AR first, then Gecko — the order generate_cht_from_ini uses, so our ordinals line up with the
            // core's own .cht dump and a diff against it stays readable.
            return ar.Concat(gecko).ToList();
        }

        /// <summary>The lines of one INI section, normalized the way <c>IniFile::Section::GetLines</c> hands
        /// them to the cheat loaders: whitespace-stripped, a leading '#' dropping the whole line and a later
        /// '#' truncating it. Gecko codes are compared VERBATIM against this text, so this normalization is
        /// part of the wire format.</summary>
        private static List<string> SectionLines(string text, string section)
        {
            var lines = new List<string>();
            string? current = null;
            foreach (var raw in text.Split('\n'))
            {
                var s = raw.Trim('\r', ' ', '\t');
                if (s.Length > 1 && s[0] == '[' && s[^1] == ']') { current = s[1..^1]; continue; }
                if (!string.Equals(current, section, StringComparison.Ordinal)) continue;

                int hash = s.IndexOf('#');
                if (hash == 0) continue;
                if (hash > 0) s = s[..hash].Trim();
                lines.Add(s);
            }
            return lines;
        }

        // ActionReplay::LoadCodes — '$' starts a code, everything else is an op line. Dolphin takes the name
        // as substr(1) with NO trimming and no '[creator]' split (unlike Gecko), so neither do we.
        private static List<Cheat> ParseActionReplay(List<string> lines, out int skipped)
        {
            var codes = new List<Cheat>();
            string? name = null;
            var ops = new List<string>();
            bool unreproducible = false;
            int dropped = 0;

            void Flush()
            {
                if (name != null && ops.Count > 0)
                {
                    if (unreproducible) dropped++;
                    else codes.Add(new Cheat(name, string.Join("+", ops), "ar"));
                }
                name = null; ops.Clear(); unreproducible = false;
            }

            foreach (var line in lines)
            {
                if (line.Length == 0) continue;
                if (line[0] == '$') { Flush(); name = line[1..]; continue; }
                if (TryOp(line, out var op)) ops.Add(op);
                else unreproducible = true;   // encrypted line (or junk) — the whole code is unusable to us
            }
            Flush();
            skipped = dropped;
            return codes;
        }

        /// <summary>An AR op line: two 8-digit hex words. Re-emitted uppercase with a single space, because
        /// that is what Dolphin formats its parsed integers back into.</summary>
        private static bool TryOp(string line, out string formatted)
        {
            formatted = "";
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0].Length != 8 || parts[1].Length != 8) return false;
            if (!uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var addr)) return false;
            if (!uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var val)) return false;
            formatted = addr.ToString("X8", CultureInfo.InvariantCulture) + " " + val.ToString("X8", CultureInfo.InvariantCulture);
            return true;
        }

        // Gecko::LoadCodes (the INI variant) — '$' or '+$' starts a code (name runs to '[' and is trimmed),
        // '*' is a note, and EVERY other line is kept verbatim as a code line. That last part is deliberate on
        // Dolphin's side: a line it cannot parse as hex is still stored as original_line, so it still takes
        // part in the serialization we have to reproduce.
        private static List<Cheat> ParseGecko(List<string> lines)
        {
            var codes = new List<Cheat>();
            string? name = null;
            var body = new List<string>();

            void Flush()
            {
                if (!string.IsNullOrEmpty(name) && body.Count > 0)
                    codes.Add(new Cheat(name!, string.Join("+", body), "gecko"));
                name = null; body.Clear();
            }

            foreach (var line in lines)
            {
                if (line.Length == 0) continue;
                if (line[0] == '$' || line[0] == '+')
                {
                    Flush();
                    var rest = line[0] == '+' && line.Length > 1 && line[1] == '$' ? line[2..] : line[1..];
                    int bracket = rest.IndexOf('[');
                    name = (bracket >= 0 ? rest[..bracket] : rest).Trim();
                    continue;
                }
                if (line[0] == '*') continue;   // note
                body.Add(line);
            }
            Flush();
            return codes;
        }

    }
}
