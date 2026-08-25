namespace MovieTheater.BooksHost.Web
{
    /// <summary>
    /// The StreamGateway's CORS stance, for the media plane: an allow-list (the site origin, and nothing else
    /// until a cast receiver exists for Books), exactly ONE Access-Control-Allow-Origin, <c>Vary: Origin</c>,
    /// 204 on preflight. Needed because the canvas reader draws pages with <c>crossOrigin="anonymous"</c>;
    /// plain <c>&lt;img&gt;</c> thumbnails need none of it but get the same headers.
    /// </summary>
    public static class HostCors
    {
        public static IApplicationBuilder UseHostCors(this IApplicationBuilder app, string siteOrigin)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { siteOrigin.TrimEnd('/') };
            return app.Use(async (context, next) =>
            {
                var origin = context.Request.Headers.Origin.ToString().TrimEnd('/');
                var headers = context.Response.Headers;
                headers["Access-Control-Allow-Origin"] = allowed.Contains(origin) ? origin : siteOrigin.TrimEnd('/');
                headers["Vary"] = "Origin";
                headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
                headers["Access-Control-Allow-Headers"] = "Range";
                headers["Access-Control-Expose-Headers"] = "Content-Length, Content-Range, Accept-Ranges, ETag";
                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }
                await next();
            });
        }
    }
}
