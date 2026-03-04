using System.Linq;
using HotChocolate;
using HotChocolate.Types;
using MovieTheater.Gql.Attributes;
using MovieTheater.Db;

namespace MovieTheater.Gql
{
    // Marker attribute scanned by GqlServiceExtensions.AddTypeExtensions()
    [HotChocolateTypeExtension]
    public class RatingQueryExtensions : ObjectTypeExtension
    {
        protected override void Configure(IObjectTypeDescriptor descriptor)
        {
            descriptor.Name("Query");

            descriptor.Field("ratingMPAs")
                .Type<ListType<ObjectType<RatingMPA>>>()
                .Resolve(ctx => ctx.Service<MovieDb>().RatingMpas.ToList());

            descriptor.Field("ratingMaps")
                .Type<ListType<ObjectType<RatingMap>>>()
                .Resolve(ctx => ctx.Service<MovieDb>().RatingMaps.ToList());
        }
    }
}