using Microsoft.Owin;
using Owin;

[assembly: OwinStartupAttribute(typeof(MovieTheater.Startup))]
namespace MovieTheater
{
    public partial class Startup
    {
        public void Configuration(IAppBuilder app)
        {
        }
    }
}
