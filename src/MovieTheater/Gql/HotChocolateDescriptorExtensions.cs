using HotChocolate.Types;
using MovieTheater.Db;

namespace MovieTheater.Gql
{
    public static class HotChocolateDescriptorExtensions
    {
        public static IObjectFieldDescriptor UseLinqExtensions(this IObjectFieldDescriptor descriptor)
            => descriptor.UseDbContext<MovieDb>()
                    .UseProjection()
                    .UseFiltering()
                    .UseSorting();
    }
}
