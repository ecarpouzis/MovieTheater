using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MovieTheater.Books.Migration
{
    /// <summary>
    /// docs/books/v2-mapping.json, parsed. The migration engine and the verifier read the v2 catalog (which
    /// tables live in which file, their columns and keys), the stage order, and the v1 table → stage index
    /// from here rather than from hand-written lists, so the approved contract is what runs.
    /// </summary>
    public sealed class MappingContract
    {
        public sealed record Column(string Name, string SqlType, string? ForeignKey, bool Nullable, bool Unique);

        public sealed record Table(string Name, string File, IReadOnlyList<string> PrimaryKey, IReadOnlyList<Column> Columns, string Purpose);

        public sealed record V1Table(string Name, IReadOnlyList<string> Targets, string Stage, IReadOnlyDictionary<string, string> ColumnRules, string? DropReason);

        public IReadOnlyDictionary<string, Table> V2 { get; }
        public IReadOnlyDictionary<string, V1Table> V1 { get; }
        public IReadOnlyList<string> Stages { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Enums { get; }

        private static readonly Regex ColumnRe = new(@"^(\w+)\s+(INTEGER|TEXT|REAL)((?:\s+\w+)*)$", RegexOptions.Compiled);

        public static MappingContract Load()
        {
            var asm = typeof(MappingContract).Assembly;
            using var stream = asm.GetManifestResourceStream("MovieTheater.Books.v2-mapping.json")
                ?? throw new InvalidOperationException("embedded v2-mapping.json missing");
            using var doc = JsonDocument.Parse(stream);
            return new MappingContract(doc.RootElement);
        }

        public static MappingContract LoadFromFile(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return new MappingContract(doc.RootElement);
        }

        private MappingContract(JsonElement root)
        {
            var v2 = new Dictionary<string, Table>(StringComparer.Ordinal);
            foreach (var t in root.GetProperty("v2").EnumerateObject())
            {
                var spec = t.Value;
                var cols = new List<Column>();
                foreach (var raw in spec.GetProperty("cols").GetString()!.Split(','))
                {
                    var part = raw.Trim();
                    if (part.StartsWith("rowid=", StringComparison.Ordinal)) continue;
                    var m = ColumnRe.Match(part);
                    if (!m.Success) throw new InvalidOperationException($"{t.Name}: cannot parse column spec '{part}'");
                    var rest = m.Groups[3].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string? fk = null;
                    var fkAt = Array.IndexOf(rest, "FK");
                    if (fkAt >= 0) fk = rest[fkAt + 1];
                    cols.Add(new Column(m.Groups[1].Value, m.Groups[2].Value, fk, rest.Contains("NULL"), rest.Contains("UNIQUE")));
                }
                v2[t.Name] = new Table(t.Name, spec.GetProperty("file").GetString()!,
                    spec.GetProperty("pk").EnumerateArray().Select(x => x.GetString()!).ToList(), cols,
                    spec.GetProperty("purpose").GetString() ?? "");
            }
            V2 = v2;

            var v1 = new Dictionary<string, V1Table>(StringComparer.Ordinal);
            foreach (var t in root.GetProperty("v1").EnumerateObject())
            {
                var spec = t.Value;
                var rules = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var c in spec.GetProperty("cols").EnumerateObject()) rules[c.Name] = c.Value.GetString() ?? "";
                v1[t.Name] = new V1Table(t.Name,
                    spec.GetProperty("targets").EnumerateArray().Select(x => x.GetString()!).ToList(),
                    spec.GetProperty("stage").GetString() ?? "-", rules,
                    spec.TryGetProperty("drop", out var d) ? d.GetString() : null);
            }
            V1 = v1;
            Stages = root.GetProperty("stages").EnumerateArray().Select(x => x.GetString()!).ToList();
            var enums = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var e in root.GetProperty("enums").EnumerateObject())
                enums[e.Name] = e.Value.EnumerateArray().Select(x => x.GetString()!).ToList();
            Enums = enums;
        }

        public IEnumerable<Table> TablesIn(string file) => V2.Values.Where(t => t.File == file);

        /// <summary>v1 tables whose rows a stage copies, in catalog order.</summary>
        public IEnumerable<V1Table> V1TablesForStage(string stage) => V1.Values.Where(t => t.Stage == stage);
    }
}
