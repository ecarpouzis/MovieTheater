using CliFx;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MovieTheater.Core;
using MovieTheater.Core.Logging;
using MovieTheater.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MovieTheater
{
    public class Program
    {
        private readonly MovieTheaterConfiguration config;
        private readonly ILogger logger;

        public static async Task Main()
        {
            var p = new Program();
            await p.RunAsync();
        }

        private Program()
        {
            // Bind configuration
            var rawConfig = BuildConfiguration();
            config = new MovieTheaterConfiguration(rawConfig);

            // Create logger for Program
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddMovieTheaterLogging();
            var serviceProvider = serviceCollection.BuildServiceProvider();
            logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        }

        private async Task RunAsync()
        {
            var app = new CliApplicationBuilder()
                .AddCommandsFromThisAssembly()
                .UseTypeActivator(GetTypeActivator())
                .Build();

            try
            {
                logger.LogInformation("Beginning command");
                await app.RunAsync();
                logger.LogInformation("Command completed successfully");
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "Unhandled exception");
                Environment.Exit(1);
            }
        }

        private Func<Type, object> GetTypeActivator()
        {
            return TypeActivator;

            object TypeActivator(Type t)
            {
                var ctor = t.GetConstructor(new Type[] { typeof(MovieTheaterConfiguration) });

                if (ctor == null)
                {
                    throw new InvalidOperationException("No constructor found that accepts MovieTheaterConfiguration parameter");
                }

                return ctor.Invoke(new object[] { config });
            }
        }

        private static IConfiguration BuildConfiguration()
        {
            IConfigurationBuilder builder = new ConfigurationBuilder();
            builder.AddJsonFile("appsettings.json");

            var aspEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if (!string.IsNullOrEmpty(aspEnv))
            {
                builder.AddJsonFile($"appsettings.{aspEnv}.json");
            }

            builder.AddEnvironmentVariables();
            return builder.Build();
        }
    }
}
