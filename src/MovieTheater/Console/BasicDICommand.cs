using Microsoft.Extensions.DependencyInjection;
using MovieTheater.Services;
using System;

namespace MovieTheater.Console
{
    public abstract class BasicDICommand
    {
        private readonly IServiceProvider services;

        protected BasicDICommand(MovieTheaterConfiguration config)
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddMovieTheaterServices(config);

            services = serviceCollection.BuildServiceProvider();
        }

        protected T GetRequiredService<T>() => services.GetRequiredService<T>();
    }
}
