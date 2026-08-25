using CliFx;
using Microsoft.Extensions.Configuration;

namespace MovieTheater.BooksHost
{
    /// <summary>
    /// The CliFx shell, shaped like the API's <c>Program</c>: configuration is bound once, every command is
    /// activated through the one constructor that takes <see cref="BooksHostConfiguration"/>, and the process
    /// exit code is the command's (a verb that refuses to act exits non-zero so a driver script stops).
    /// </summary>
    public class Program
    {
        private readonly BooksHostConfiguration config;

        public static async Task<int> Main()
        {
            var p = new Program();
            return await p.RunAsync();
        }

        private Program()
        {
            config = new BooksHostConfiguration(BuildConfiguration());
        }

        private async Task<int> RunAsync()
        {
            var app = new CliApplicationBuilder()
                .AddCommandsFromThisAssembly()
                .UseTypeActivator(TypeActivator)
                .Build();
            try
            {
                return await app.RunAsync();
            }
            catch (Exception e)
            {
                await Console.Error.WriteLineAsync("Unhandled exception: " + e);
                return 1;
            }
        }

        private object TypeActivator(Type t)
        {
            var ctor = t.GetConstructor(new[] { typeof(BooksHostConfiguration) })
                ?? throw new InvalidOperationException($"{t.Name} needs a constructor taking BooksHostConfiguration");
            return ctor.Invoke(new object[] { config });
        }

        public static IConfiguration BuildConfiguration()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true);
            var aspEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if (!string.IsNullOrEmpty(aspEnv)) builder.AddJsonFile($"appsettings.{aspEnv}.json", optional: true);
            builder.AddEnvironmentVariables();
            return builder.Build();
        }
    }
}
