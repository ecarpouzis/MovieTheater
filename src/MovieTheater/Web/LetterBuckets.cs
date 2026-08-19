using System.Collections.Generic;

namespace MovieTheater.Web
{
    /// <summary>
    /// The A–Z bucket walk shared by the letter-pager endpoints (movies <c>/API/BrowseLetters</c>,
    /// arcade <c>/API/Arcade/GameLetters</c>): given the ALREADY-ORDERED sort keys of a card list,
    /// produce per-letter counts and first offsets. Offsets are counted by walking the ordered list
    /// itself rather than by ordering buckets — so they agree with SQL's collation instead of with
    /// an assumption about it, and "first offset wins" if a letter turns out not to be contiguous
    /// (a collation can sort some punctuation between letters): jumping to the bucket's first card
    /// is still right. Only meaningful under an alphabetical sort — the clients never ask otherwise.
    /// </summary>
    public static class LetterBuckets
    {
        public sealed record Bucket(string Letter, int Count, int Offset);

        /// <summary>The A–Z bucket a sort key files under. Anything not starting A–Z (numbers,
        /// punctuation, empty) is "#".</summary>
        public static string LetterOf(string? sortKey)
        {
            if (string.IsNullOrEmpty(sortKey)) return "#";
            var c = char.ToUpperInvariant(sortKey[0]);
            return c >= 'A' && c <= 'Z' ? c.ToString() : "#";
        }

        public static List<Bucket> Walk(IReadOnlyList<string?> orderedKeys)
        {
            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            var offsets = new Dictionary<string, int>();
            for (int i = 0; i < orderedKeys.Count; i++)
            {
                var letter = LetterOf(orderedKeys[i]);
                if (!counts.ContainsKey(letter))
                {
                    order.Add(letter);
                    counts[letter] = 0;
                    offsets[letter] = i;
                }
                counts[letter]++;
            }
            var buckets = new List<Bucket>(order.Count);
            foreach (var l in order) buckets.Add(new Bucket(l, counts[l], offsets[l]));
            return buckets;
        }
    }
}
