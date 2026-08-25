using Microsoft.EntityFrameworkCore.Design;

namespace MovieTheater.Books.Db
{
    /// <summary>
    /// <c>dotnet ef migrations add X --context BooksDb --output-dir Migrations/Hot</c> from src/MovieTheater.Books.Db.
    /// The file path only matters for <c>database update</c>; migrations are generated from the model.
    /// </summary>
    public sealed class DesignTimeBooksDbFactory : IDesignTimeDbContextFactory<BooksDb>
    {
        public BooksDb CreateDbContext(string[] args) =>
            new BooksDb(BooksDbOptions.Hot(Environment.GetEnvironmentVariable("BOOKS_DB") ?? "books.design.db"));
    }

    public sealed class DesignTimeBooksLegsDbFactory : IDesignTimeDbContextFactory<BooksLegsDb>
    {
        public BooksLegsDb CreateDbContext(string[] args) =>
            new BooksLegsDb(BooksDbOptions.Legs(Environment.GetEnvironmentVariable("BOOKS_LEGS_DB") ?? "books-legs.design.db"));
    }
}
