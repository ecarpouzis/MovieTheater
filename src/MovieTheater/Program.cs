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

        public static async Task<int> Main()
        {
            var p = new Program();
            return await p.RunAsync();
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

        /// <summary>Runs the CLI and returns its PROCESS EXIT CODE.</summary>
        /// <remarks>
        /// CliFx handles a <c>CommandException</c> itself — it prints the message and RETURNS the exit code
        /// rather than throwing — so the <c>catch</c> below never sees one. This used to discard
        /// <c>app.RunAsync()</c>'s result and return <c>Task</c>, which meant EVERY command reported success
        /// no matter how it ended: a command that deliberately refused to act (e.g. arcade-romcache-export's
        /// dependency-closure guard) exited 0 and any driver script sailed straight past it. Propagate the
        /// code, and only claim success when it is actually 0.
        /// </remarks>
        private async Task<int> RunAsync()
        {
            var app = new CliApplicationBuilder()
                .AddCommandsFromThisAssembly()
                .UseTypeActivator(GetTypeActivator())
                .Build();

            try
            {
                logger.LogInformation("Beginning command");
                var exitCode = await app.RunAsync();
                if (exitCode == 0) logger.LogInformation("Command completed successfully");
                else logger.LogError("Command failed with exit code {ExitCode}", exitCode);
                return exitCode;
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "Unhandled exception");
                return 1;
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
