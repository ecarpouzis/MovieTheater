using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace MovieTheater
{
    public static class LoggingExtensions
    {
        public static IServiceCollection AddMovieLogging(this IServiceCollection services)
        {
            var serilog = new LoggerConfiguration()
                .WriteTo.Console(outputTemplate: "[{SourceContext}][{Timestamp:yyyy-MM-dd HH:mm:ss}][{Level:u3}] {Message:l}{NewLine}{Exception}")
                .CreateLogger();

            services.AddLogging(log => log.AddSerilog(logger: serilog));

            return services;
        }
    }
}
