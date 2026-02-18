using System.Collections.Generic;
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

            // These resolvers return empty lists when exporting schema.
            // Later replace with proper EF-backed resolvers or service calls.
            descriptor.Field("ratingMPAs")
                .Type<ListType<ObjectType<RatingMPA>>>()
                .Resolve(ctx => new List<RatingMPA>());

            descriptor.Field("ratingMaps")
                .Type<ListType<ObjectType<RatingMap>>>()
                .Resolve(ctx => new List<RatingMap>());
        }
    }
}