using HotChocolate;
using HotChocolate.Types;
using MovieTheater.Db;
using MovieTheater.Gql.Attributes;
using System.Collections.Generic;
using System.Linq;

namespace MovieTheater.Gql.Movie
{
    [HotChocolateQuery]
    public class MovieQuery
    {
        
        public IQueryable<MovieTheater.Db.Movie> GetMovies([Service] MovieDb movieDb) => movieDb.Movies;
        public IEnumerable<MovieTheater.Db.Movie> GetRandomMovies([Service] MovieDb movieDb, int take = 50 ) =>
                                                movieDb.Movies.OrderBy(elem => System.Guid.NewGuid()).Take(take);
    }

    [HotChocolateTypeExtension]
    public class MovieQueryDescriptor : ObjectTypeExtension<MovieQuery>
    {
        protected override void Configure(IObjectTypeDescriptor<MovieQuery> descriptor)
        {
            descriptor.Field(x => x.GetMovies(default))
                .UseLinqExtensions();

            descriptor.Field(x => x.GetRandomMovies(default, default));
        }
    }
}
