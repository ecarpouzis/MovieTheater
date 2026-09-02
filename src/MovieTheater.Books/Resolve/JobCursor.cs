using System.Globalization;
using MovieTheater.Books.Migration;

namespace MovieTheater.Books.Resolve
{
    /// <summary>
    /// The persisted cursor of a <see cref="TargetWriter"/>-driven derived job, kept in <c>SystemState</c> under
    /// the SAME key the admin's <c>POST /admin/recompute/{what}</c> pages with, so the CLI verb and the admin
    /// route resume each other. Written inside the batch's own transaction — a kill between pages loses
    /// nothing — and cleared when the run completes, so a later full run starts clean. Under a dry-run writer
    /// the writes are no-ops by construction.
    /// </summary>
    public static class JobCursor
    {
        public static long Read(TargetWriter hot, string key) =>
            hot.Scalar<long>("SELECT CAST(coalesce(Value, '0') AS INTEGER) FROM SystemState WHERE Key = $k", ("$k", key));

        public static void Write(TargetWriter hot, string key, long value) =>
            hot.Upsert("SystemState", new { Key = key, Value = value.ToString(CultureInfo.InvariantCulture) });

        public static void Clear(TargetWriter hot, string key) =>
            hot.Exec("DELETE FROM SystemState WHERE Key = $k", ("$k", key));
    }
}
