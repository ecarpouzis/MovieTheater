using Microsoft.EntityFrameworkCore;

namespace MovieTheater.Books.Db
{
    /// <summary>
    /// The FTS5 index over the resolved catalog text. Content-less (<c>content=''</c>): rowid = Item.Id and the
    /// only column is <c>body</c>, so a hit is an item id and nothing else — the browse query joins Item for
    /// everything shown. Not an EF entity: SQLite virtual tables have no fixed columns EF can model, so the
    /// migration creates it with raw SQL and these helpers are the only code that names it.
    /// </summary>
    public static class ItemFts
    {
        public const string Table = "ItemFts";

        public const string CreateSql =
            "CREATE VIRTUAL TABLE IF NOT EXISTS ItemFts USING fts5(body, content='', tokenize='unicode61 remove_diacritics 2');";

        public const string DropSql = "DROP TABLE IF EXISTS ItemFts;";

        /// <summary>Content-less tables cannot be truncated by DELETE; this is the documented reset.</summary>
        public const string ClearSql = "INSERT INTO ItemFts(ItemFts) VALUES('delete-all');";

        public const string InsertSql = "INSERT INTO ItemFts(rowid, body) VALUES ($id, $body);";

        public const string OptimizeSql = "INSERT INTO ItemFts(ItemFts) VALUES('optimize');";

        /// <summary>Ids matching an FTS5 query, best-rank first. The caller escapes user input into MATCH syntax.</summary>
        public static IQueryable<int> Search(BooksDb db, string match, int limit) =>
            db.Database.SqlQueryRaw<int>("SELECT rowid AS \"Value\" FROM ItemFts WHERE ItemFts MATCH {0} ORDER BY rank LIMIT {1}", match, limit);
    }
}
