using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using MovieTheater.Services;
using System.Threading.Tasks;

namespace MovieTheater.Web
{
    [Command(name: "web", Description = "Run the web api")]
    public class WebHostCommand : ICommand
    {
        private readonly MovieTheaterConfiguration config;

        public WebHostCommand(MovieTheaterConfiguration config)
        {
            this.config = config;
        }

        public async ValueTask ExecuteAsync(IConsole console)
        {
            await new WebHostBuilder()
                .UseConfiguration(config.RawConfiguration)
                .ConfigureServices(services => services.AddMovieTheaterServices(config))
                .UseStartup(_ => new Startup(config))
                .UseKestrel()
                .CaptureStartupErrors(true)
                .Build()
                .RunAsync();
        }
    }
}
