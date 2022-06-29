using Microsoft.Extensions.DependencyInjection;

namespace MovieTheater.Services.Python
{
    public static class PythonServiceExtensions
    {
        public static IServiceCollection AddPythonService(this IServiceCollection services, string pyPath)
        {
            services.Configure<PythonOptions>(options => options.PyPath = pyPath ?? "python");
            services.AddTransient<PythonClient>();

            return services;
        }
    }
}
