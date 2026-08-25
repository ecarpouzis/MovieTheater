using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace MovieTheater.Books.Migration
{
    /// <summary>
    /// Idempotent row writer over one v2 file. <see cref="Upsert"/> is <c>INSERT … ON CONFLICT(pk) DO UPDATE SET</c>
    /// over exactly the columns supplied — so re-running a batch after a kill converges instead of duplicating
    /// or clobbering columns another stage owns (the user-activity stage merges three v1 tables into one row
    /// this way). The primary key of every table comes from the mapping contract. One transaction per batch.
    /// </summary>
    public sealed class TargetWriter : IDisposable
    {
        private readonly SqliteConnection conn;
        private readonly MappingContract mapping;
        private readonly bool dryRun;
        private SqliteTransaction? tx;
        private readonly Dictionary<string, SqliteCommand> commands = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Props = new();

        public TargetWriter(string path, MappingContract mapping, bool dryRun)
        {
            this.mapping = mapping;
            this.dryRun = dryRun;
            conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = dryRun ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
            conn.Open();
            if (!dryRun)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA busy_timeout=10000; PRAGMA cache_size=-65536; PRAGMA temp_store=MEMORY; PRAGMA synchronous=NORMAL;";
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>The ASCII unit separator, used to pack several columns into one string for Pairs() reads (char(31) in SQL).</summary>
        public const char Sep = (char)31;

        public SqliteConnection Connection => conn;

        /// <summary>A command bound to the current transaction — Microsoft.Data.Sqlite refuses to execute a command whose
        /// Transaction is unset while the connection has one open.</summary>
        public SqliteCommand CreateCommand(string sql)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            return cmd;
        }
        public int Writes { get; private set; }

        public void Begin()
        {
            if (dryRun) return;
            tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // children may precede parents inside one batch (folders, collection nodes); FKs are checked at commit
            cmd.CommandText = "PRAGMA defer_foreign_keys=ON;";
            cmd.ExecuteNonQuery();
        }

        public void Commit()
        {
            if (tx == null) return;
            tx.Commit();
            tx.Dispose();
            tx = null;
        }

        public void Rollback()
        {
            if (tx == null) return;
            tx.Rollback();
            tx.Dispose();
            tx = null;
        }

        /// <summary>Insert-or-update the supplied columns of one row. The anonymous object's property names are the columns.</summary>
        public void Upsert(string table, object values)
        {
            Writes++;
            if (dryRun) return;
            var props = Props.GetOrAdd(values.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance));
            var key = table + "|" + values.GetType().FullName;
            if (!commands.TryGetValue(key, out var cmd))
            {
                var pk = mapping.V2[table].PrimaryKey;
                var cols = props.Select(p => p.Name).ToList();
                foreach (var k in pk) if (!cols.Contains(k)) throw new InvalidOperationException($"{table} upsert lacks key column {k}");
                var nonKey = cols.Where(c => !pk.Contains(c)).ToList();
                var sql = $"INSERT INTO \"{table}\" ({string.Join(",", cols.Select(c => '"' + c + '"'))}) VALUES ({string.Join(",", cols.Select(c => "$" + c))})"
                          + $" ON CONFLICT({string.Join(",", pk.Select(c => '"' + c + '"'))}) DO "
                          + (nonKey.Count == 0 ? "NOTHING" : "UPDATE SET " + string.Join(",", nonKey.Select(c => $"\"{c}\"=excluded.\"{c}\"")));
                cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var c in cols) cmd.Parameters.Add("$" + c, SqliteType.Text);
                commands[key] = cmd;
            }
            cmd.Transaction = tx;
            for (var i = 0; i < props.Length; i++)
                cmd.Parameters[i].Value = ToDb(props[i].GetValue(values));
            cmd.ExecuteNonQuery();
        }

        /// <summary>UPDATE the supplied columns of an existing row by its single-column key.</summary>
        public void Update(string table, string keyColumn, object keyValue, object values)
        {
            Writes++;
            if (dryRun) return;
            var props = Props.GetOrAdd(values.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance));
            var key = table + "|U|" + keyColumn + "|" + values.GetType().FullName;
            if (!commands.TryGetValue(key, out var cmd))
            {
                cmd = conn.CreateCommand();
                cmd.CommandText = $"UPDATE \"{table}\" SET {string.Join(",", props.Select(p => $"\"{p.Name}\"=${p.Name}"))} WHERE \"{keyColumn}\"=$__key";
                foreach (var p in props) cmd.Parameters.Add("$" + p.Name, SqliteType.Text);
                cmd.Parameters.Add("$__key", SqliteType.Text);
                commands[key] = cmd;
            }
            cmd.Transaction = tx;
            for (var i = 0; i < props.Length; i++) cmd.Parameters[i].Value = ToDb(props[i].GetValue(values));
            cmd.Parameters[props.Length].Value = ToDb(keyValue);
            cmd.ExecuteNonQuery();
        }

        public int Exec(string sql, params (string, object?)[] args)
        {
            if (dryRun) return 0;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, ToDb(v));
            return cmd.ExecuteNonQuery();
        }

        public T Scalar<T>(string sql, params (string, object?)[] args)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, ToDb(v));
            var o = cmd.ExecuteScalar();
            if (o is null || o is DBNull) return default!;
            return (T)Convert.ChangeType(o, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        public List<(long, string?)> Pairs(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            var list = new List<(long, string?)>();
            while (r.Read()) list.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1)));
            return list;
        }

        public static object ToDb(object? v) => v switch
        {
            null => DBNull.Value,
            bool b => b ? 1 : 0,
            Enum e => Convert.ToInt32(e),
            DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture),
            _ => v,
        };

        public void Dispose()
        {
            Rollback();
            foreach (var c in commands.Values) c.Dispose();
            conn.Dispose();
        }
    }
}
