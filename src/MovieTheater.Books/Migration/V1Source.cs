using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MovieTheater.Books.Migration
{
    /// <summary>
    /// The frozen v1 SQLite file, opened READ-ONLY. Every stage pages its driving table by <c>rowid</c>
    /// (<c>WHERE rowid &gt; @cursor ORDER BY rowid LIMIT @n</c>) — the cursor and the ordering are the same
    /// column by construction, which is the resumability rule.
    /// </summary>
    public sealed class V1Source : IDisposable
    {
        private readonly SqliteConnection conn;

        public V1Source(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("v1 source not found", path);
            conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA query_only=1; PRAGMA cache_size=-65536; PRAGMA temp_store=MEMORY;";
            cmd.ExecuteNonQuery();
        }

        public string Path => conn.DataSource;

        public bool TableExists(string table) =>
            Scalar<long>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$t", ("$t", table)) > 0;

        public long Count(string table, string? where = null, params (string, object?)[] args) =>
            Scalar<long>($"SELECT count(*) FROM \"{table}\"" + (where == null ? "" : " WHERE " + where), args);

        public T Scalar<T>(string sql, params (string, object?)[] args)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            var o = cmd.ExecuteScalar();
            if (o is null || o is DBNull) return default!;
            return (T)Convert.ChangeType(o, typeof(T), CultureInfo.InvariantCulture);
        }

        /// <summary>One page of a driving table: rowid plus every column, in rowid order.</summary>
        public List<V1Row> Page(string table, long afterRowid, int limit, string? where = null)
        {
            var sql = $"SELECT rowid AS __rowid, * FROM \"{table}\" WHERE rowid > $c" + (where == null ? "" : " AND (" + where + ")") + " ORDER BY rowid LIMIT $n";
            return Rows(sql, ("$c", afterRowid), ("$n", limit));
        }

        public long Remaining(string table, long afterRowid, string? where = null) =>
            Scalar<long>($"SELECT count(*) FROM \"{table}\" WHERE rowid > $c" + (where == null ? "" : " AND (" + where + ")"), ("$c", afterRowid));

        public List<V1Row> Rows(string sql, params (string, object?)[] args)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var names = new string[r.FieldCount];
            for (var i = 0; i < names.Length; i++) names[i] = r.GetName(i);
            var list = new List<V1Row>();
            while (r.Read())
            {
                var values = new object?[r.FieldCount];
                for (var i = 0; i < values.Length; i++) values[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                list.Add(new V1Row(names, values));
            }
            return list;
        }

        public void Dispose() => conn.Dispose();
    }

    /// <summary>One v1 row. Accessors are tolerant: SQLite affinity means an INTEGER column can hold text.</summary>
    public sealed class V1Row
    {
        private readonly string[] names;
        private readonly object?[] values;

        public V1Row(string[] names, object?[] values) { this.names = names; this.values = values; }

        public long Rowid => L("__rowid") ?? throw new InvalidOperationException("row has no rowid");

        public bool Has(string col) => Array.IndexOf(names, col) >= 0;

        public object? Raw(string col)
        {
            var i = Array.IndexOf(names, col);
            return i < 0 ? throw new KeyNotFoundException(col) : values[i];
        }

        public string? S(string col)
        {
            var v = Raw(col);
            return v switch { null => null, string s => s, _ => Convert.ToString(v, CultureInfo.InvariantCulture) };
        }

        /// <summary>Non-empty, trimmed text or null.</summary>
        public string? T(string col)
        {
            var s = S(col)?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        public long? L(string col)
        {
            var v = Raw(col);
            return v switch
            {
                null => null,
                long l => l,
                double d => (long)d,
                string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l2) ? l2 : null,
                _ => Convert.ToInt64(v, CultureInfo.InvariantCulture),
            };
        }

        public int? I(string col) => L(col) is long l ? checked((int)l) : null;

        public int Int(string col, int fallback = 0) => I(col) ?? fallback;

        public bool B(string col) => (L(col) ?? 0) != 0;

        public double? D(string col)
        {
            var v = Raw(col);
            return v switch
            {
                null => null,
                double d => d,
                long l => l,
                string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2) ? d2 : null,
                _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
            };
        }

        public DateTime? At(string col) => Transforms.ParseDate(S(col));
    }
}
