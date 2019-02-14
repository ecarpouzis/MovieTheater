﻿using Microsoft.EntityFrameworkCore;

namespace MovieTheater.Db
{
    public class MovieDb : DbContext
    {
        public DbSet<Movie> Movies { get; set; }
        public DbSet<MoviePoster> MoviePosters { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<UserMovies> UserMovies { get; set; }
        public DbSet<Viewing> Viewings { get; set; }

        public MovieDb(DbContextOptions<MovieDb> options)
            : base(options)
        {

        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<MoviePoster>().HasKey(d => new { d.MovieID, d.PosterIndex });
        }
    }
}
