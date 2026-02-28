using System.Collections.Generic;
using HotChocolate.Types;
using MovieTheater.Gql.Attributes;
using MovieTheater.Db;

namespace MovieTheater.Gql
{
    // Marker attribute scanned by GqlServiceExtensions.AddTypeExtensions()
    [HotChocolateTypeExtension]
    public class UserSettingsQueryExtensions : ObjectTypeExtension
    {
        protected override void Configure(IObjectTypeDescriptor descriptor)
        {
            descriptor.Name("Query");

            // This resolver returns an empty list when exporting schema.
            // Later replace with proper EF-backed resolver or service call.
            descriptor.Field("userSettings")
                .Type<ListType<ObjectType<UserSettings>>>()
                .Resolve(ctx => new List<UserSettings>());
        }
    }
}
