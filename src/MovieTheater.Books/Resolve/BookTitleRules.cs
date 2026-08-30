using System.Text.RegularExpressions;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// A book's displayed title, cleaned of the artefacts of the file it came from.
    /// <para>
    /// Every book in the library reached Calibre as a file, and Calibre took the title from the file
    /// name. An audit of all 126,389 of them found 87.9% already clean and the remainder carrying a
    /// short, mechanical list of scars — a sanitised colon, the author repeated as a suffix, a format
    /// or release tag, a series index, a surviving extension, Word's own export prefix.
    /// </para>
    /// <para>
    /// This runs in <see cref="ItemResolver"/> rather than against Calibre or <c>Item.Title</c>, for
    /// two reasons. Renaming a title in Calibre renames the book's FOLDER, which invalidates the
    /// <c>Item.Path</c> every item is matched on and would force a full rescan to relink — 4,078
    /// renames at roughly 0.2s each on the share. And <c>CalibreImportService</c> copies Calibre's
    /// title over <c>Item.Title</c> on every run, so anything written there is undone by the next
    /// import. Resolving it means the rule is reapplied from source each time and stored only in
    /// <c>ResolvedTitle</c>, which is what the site actually displays.
    /// </para>
    /// <para>
    /// Two guards hold across every rule: a title is never blanked, and a title is never reduced to
    /// the book's own author — stripping there would leave a person's name standing as the title of
    /// their book.
    /// </para>
    /// </summary>
    public static class BookTitleRules
    {
        private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        /// <summary>An extension that survived into the title: "Ten Big Ones .Html".</summary>
        private static readonly Regex Extension = new(
            @"\s*\.(?:epub|mobi|azw3?|pdf|lit|rtf|txt|zip|rar|html?|docx?|prc|pdb|fb2)\s*$", Opt);

        /// <summary>A format or release tag: "(epub)", "[retail]", "(v5.0)", "(c2)".</summary>
        private static readonly Regex FormatTag = new(
            @"\s*[\(\[]\s*(?:epub|mobi|azw3?|pdf|lit|rtf|txt|zip|rar|html?|docx?|prc|pdb|fb2|" +
            @"retail|uc|ss|sipdf|scan|ocr|proofed|v\d[\d.]*|c\d+)\s*[\)\]]", Opt);

        /// <summary>A leading series index: "05 - Warrior Priest". The dash is required — "1984",
        /// "57 Chevy" and "802.11 Wireless Networks" are real titles that open with digits.</summary>
        private static readonly Regex LeadingIndex = new(@"^\s*\d{1,3}\s*[-–—]\s+(?=\S)", Opt);

        /// <summary>An '_' directly before a space is a ':' Windows could not store in a file name.</summary>
        private static readonly Regex SanitisedColon = new(@"_(\s)", Opt);

        /// <summary>A .doc exported straight out of Word keeps the application's own prefix.</summary>
        private static readonly Regex WordPrefix = new(@"^\s*Microsoft Word\s*[-–—]\s*", Opt);

        private static readonly Regex Runs = new(@"\s{2,}", Opt);
        private static readonly Regex NonAlnum = new(@"[^a-z0-9]+", Opt);

        /// <summary>Words that keep a trailing clause from reading as a person's name.</summary>
        private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","of","and","in","for","to","on","at","from","with","his",
            "her","my","our","their","is","are","was","were","not","no","book",
        };

        /// <summary>The lowercase particles a surname carries: 'de Bernieres', 'van Beethoven'.</summary>
        private static readonly HashSet<string> Particles = new(StringComparer.Ordinal)
        {
            "de","del","della","der","di","da","do","dos","du","la","le","van","von","ter",
            "ten","bin","ibn","al","el","st.","mc","mac",
        };

        /// <summary>'R.' or 'A' — a single letter, optionally with its period.</summary>
        private static bool IsInitial(string t) =>
            t.Length <= 2 && char.IsLetter(t[0]) && (t.Length == 1 || t[1] == '.');

        /// <summary>
        /// The cleaned title, plus the author it recovered if the title's trailing clause was the
        /// only place that book's author was recorded (<paramref name="knownAuthors"/> empty).
        /// Returns <paramref name="title"/> unchanged when no rule applies or a guard trips.
        /// </summary>
        public static (string? Title, string? LiftedAuthor) Clean(string? title, IReadOnlyList<string> knownAuthors)
        {
            var original = title?.Trim();
            if (string.IsNullOrEmpty(original)) return (title, null);

            var n = Extension.Replace(original, "");
            n = FormatTag.Replace(n, " ");
            n = SanitisedColon.Replace(n, ":$1");
            n = WordPrefix.Replace(n, "");
            n = LeadingIndex.Replace(n, "");

            // The author repeated as a suffix. Matched against this book's OWN author so it cannot
            // fire on a real subtitle ("Dune - Messiah" keeps its tail; "Wintersmith - Terry
            // Pratchett" does not). Twice, because a filename sometimes carries it twice over.
            string? lifted = null;
            for (var pass = 0; pass < 2; pass++)
            {
                var cut = n.LastIndexOf(" - ", StringComparison.Ordinal);
                if (cut <= 0) break;
                var head = n[..cut].Trim();
                var tail = n[(cut + 3)..].Trim();
                if (head.Length == 0) break;

                if (knownAuthors.Any(a => SameName(tail, a)))
                {
                    n = head;
                }
                else if (knownAuthors.Count == 0 && pass == 0 && LooksLikePerson(tail))
                {
                    // Calibre never matched this item, so nothing supplied an author and the
                    // filename's trailing clause is the only record of one. Keep it rather than
                    // discard it.
                    lifted = tail;
                    n = head;
                    break;
                }
                else break;
            }

            // The same repeat on the other side of the dash — 'Rachel Lindsay - An Affair To
            // Forget', and the 'Hagen, Lynn - ...' shape a "Surname, Forename" filename produces.
            // Guarded identically: the head must be this book's OWN author.
            {
                var cut = n.IndexOf(" - ", StringComparison.Ordinal);
                if (cut > 0)
                {
                    var head = n[..cut].Trim();
                    var rest = n[(cut + 3)..].Trim();
                    if (rest.Length > 0 && knownAuthors.Any(a => SameName(head, a))) n = rest;
                }
            }

            n = Extension.Replace(n, "");
            n = Runs.Replace(n, " ").Trim(' ', '-', '–', '—', ',', ';', ':', '_');

            if (n.Length < 2) return (original, null);                          // never blank a title
            if (knownAuthors.Any(a => SameName(n, a))) return (original, null); // never leave the author as the title
            if (lifted != null && SameName(n, lifted)) return (original, null);

            return (n, lifted);
        }

        /// <summary>Two spellings of one name, compared on their words alone so ordering and
        /// punctuation ("Pratchett, Terry" / "Terry Pratchett") do not separate them.</summary>
        private static bool SameName(string? a, string? b) => NameKey(a) == NameKey(b);

        private static string NameKey(string? s) =>
            string.Join(' ', NonAlnum.Split((s ?? "").ToLowerInvariant())
                                     .Where(w => w.Length > 0)
                                     .OrderBy(w => w, StringComparer.Ordinal));

        /// <summary>Whether a trailing clause reads as a person's name, and so can be lifted as the
        /// author of a book that has none.</summary>
        private static bool LooksLikePerson(string s)
        {
            if (s.Length == 0 || s.Length > 40) return false;
            if (s.Any(c => char.IsDigit(c) || c is '[' or ']' or '(' or ')')) return false;
            var toks = s.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length is < 2 or > 5) return false;
            // An INITIAL is not a word: 'R. A. Salvatore' and 'James S. A. Corey' were both read as
            // titles because 'A.' trims to the article "a".
            if (toks.Any(t => !IsInitial(t) && Stop.Contains(t.Trim('.')))) return false;
            // A name needs a capitalised first and last token; the particles in between may be
            // lowercase — 'Louis de Bernieres', 'Ursula K. Le Guin', 'Ludwig van Beethoven'.
            if (!char.IsUpper(toks[0][0]) || !char.IsUpper(toks[^1][0])) return false;
            return toks[1..^1].All(t => char.IsUpper(t[0]) || Particles.Contains(t));
        }
    }
}
