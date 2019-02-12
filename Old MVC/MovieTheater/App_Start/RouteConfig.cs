using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;

namespace MovieTheater
{
    public class RouteConfig
    {
        public static void RegisterRoutes(RouteCollection routes)
        {
            routes.IgnoreRoute("{resource}.axd/{*pathInfo}");
            routes.MapRoute("ImageHandler", "MovieImage/ImageHandler", new { controller = "Movie", action = "ImageHandler" });
            routes.MapRoute("Browse", "Browse", new { controller = "Movie", action = "Browse" });
            routes.MapRoute("Insert", "Insert", new { controller = "Movie", action = "Insert" });
            routes.MapRoute("Update", "Update", new { controller = "Movie", action = "Update" });


            routes.MapRoute(
                name: "Default",
                url: "{controller}/{action}/{id}",
                defaults: new { controller = "Movie", action = "Home", id = UrlParameter.Optional }
            );
        }
    }
}
