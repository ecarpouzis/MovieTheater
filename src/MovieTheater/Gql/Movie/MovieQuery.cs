using HotChocolate;
using HotChocolate.Types;
using MovieTheater.Db;
using MovieTheater.Gql.Attributes;
using System.Linq;

namespace MovieTheater.Gql.Movie
{
    [HotChocolateQuery]
    public class MovieQuery
    {
        public IQueryable<MovieTheater.Db.Movie> GetMovies([Service] MovieDb movieDb) => movieDb.Movies;
    }

    [HotChocolateTypeExtension]
    public class MovieQueryDescriptor : ObjectTypeExtension<MovieQuery>
    {
        protected override void Configure(IObjectTypeDescriptor<MovieQuery> descriptor)
        {
            descriptor.Field(x => x.GetMovies(default))
                .UseLinqExtensions();
        }
    }
}
