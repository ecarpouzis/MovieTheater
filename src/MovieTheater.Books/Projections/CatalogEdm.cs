using Microsoft.AspNetCore.OData.Query;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder;
using Microsoft.OData.UriParser;

namespace MovieTheater.Books.Projections
{
    /// <summary>
    /// The one EDM description of <see cref="ItemSummary"/>, shared by the OData catalog's <c>[EnableQuery]</c> and
    /// by the ad-hoc <c>$filter</c> the grouped-browse endpoints parse. There is no OData route and no metadata
    /// document — this exists purely so both surfaces speak the SAME filter vocabulary.
    ///
    /// <para><b>Lower camel case is load-bearing.</b> The client writes its filter against the JSON it received, so
    /// the filter vocabulary has to be the JSON vocabulary: without it <c>$filter=year eq 1987</c> is rejected as
    /// "no such property" on one endpoint while working on the other — the exact drift this type prevents.</para>
    /// </summary>
    public static class CatalogEdm
    {
        public static readonly IEdmModel Model = Build();

        private static IEdmModel Build()
        {
            var builder = new ODataConventionModelBuilder();
            builder.EntitySet<ItemSummary>("catalog");
            builder.EnableLowerCamelCase();
            return builder.GetEdmModel();
        }

        /// <summary>
        /// Apply one <c>$filter</c> string to a summary query. Used by the grouped-browse endpoints (which have to
        /// filter before they can GROUP BY) and by the catalog's total-count path — one parser, one vocabulary.
        /// </summary>
        public static IQueryable<ItemSummary> ApplyFilter(IQueryable<ItemSummary> query, string filter)
        {
            var context = new ODataQueryContext(Model, typeof(ItemSummary), new ODataPath());
            var parser = new ODataQueryOptionParser(Model, context.ElementType, context.NavigationSource,
                new Dictionary<string, string> { ["$filter"] = filter });
            var option = new FilterQueryOption(filter, context, parser);
            return (IQueryable<ItemSummary>)option.ApplyTo(query, new ODataQuerySettings());
        }
    }
}
