using HotChocolate;
using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Core;
using MovieTheater.Gql.Attributes;
using System.Linq;

namespace MovieTheater.Gql
{
    public static class GqlServiceExtensions
    {
        public static NameString SchemaName => Schema.DefaultName;

        public static IServiceCollection AddMovieTheaterGql(this IServiceCollection services)
        {
            services.AddGraphQLServer(schemaName: SchemaName)
                .AddAuthorization()
                .AddProjections()
                .AddFiltering()
                .AddSorting()
                .BindRuntimeType<object, HotChocolate.Types.AnyType>()
                .AddQueryType<Query>()
                .AddMutationType<Mutation>()
                .AddTypeExtensions()
                .AddErrorFilter(ErrorFilter);

            return services;
        }

        private static IRequestExecutorBuilder AddTypeExtensions(this IRequestExecutorBuilder builder)
        {
            var allAssemblyTypes = typeof(GqlServiceExtensions).Assembly.GetTypes();

            foreach (var type in allAssemblyTypes)
            {
                var attributes = type.GetCustomAttributes(typeof(HotChocolateTypeExtensionAttribute), inherit: false);
                if (attributes.Any())
                    builder.AddTypeExtension(type);
            }

            return builder;
        }

        private static IError ErrorFilter(IError error)
        {
            if (error.Exception is BusinessException businessException)
            {
                return error.WithMessage(businessException.Message);
            }

            return error;
        }
    }
}
