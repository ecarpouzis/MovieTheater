using System.Globalization;
using System.Text;
using System.Xml;

namespace MovieTheater.Books.Opds
{
    /// <summary>
    /// The vocabulary of an OPDS document: namespaces, media types and link relations, named once so a writer
    /// and a test cannot disagree about a string an e-reader matches exactly.
    /// </summary>
    public static class OpdsXml
    {
        public const string AtomNs = "http://www.w3.org/2005/Atom";
        public const string OpdsNs = "http://opds-spec.org/2010/catalog";

        /// <summary>
        /// OPDS Page Streaming Extension. Clients that understand it (Chunky, Panels, KyBook, Moon+) fetch one
        /// page at a time instead of pulling the whole archive, which is the only way reading a 200 MB collected
        /// edition over a phone connection is tolerable.
        /// </summary>
        public const string PseNs = "http://vaemendis.net/opds-pse/ns";
        public const string DcNs = "http://purl.org/dc/terms/";
        public const string OpenSearchNs = "http://a9.com/-/spec/opensearch/1.1/";

        public const string CatalogType = "application/atom+xml;profile=opds-catalog";
        public const string NavigationType = CatalogType + ";kind=navigation";
        public const string AcquisitionType = CatalogType + ";kind=acquisition";

        /// <summary>Response content types. Charset is explicit — see <see cref="Utf8StringWriter"/>.</summary>
        public const string NavigationContentType = NavigationType + ";charset=utf-8";
        public const string AcquisitionContentType = AcquisitionType + ";charset=utf-8";
        public const string OpenSearchContentType = "application/opensearchdescription+xml;charset=utf-8";
        public const string OpenSearchLinkType = "application/opensearchdescription+xml";

        public const string AcquisitionRel = "http://opds-spec.org/acquisition";
        public const string ImageRel = "http://opds-spec.org/image";
        public const string ThumbnailRel = "http://opds-spec.org/image/thumbnail";
        public const string PseStreamRel = "http://vaemendis.net/opds-pse/stream";

        /// <summary>
        /// StringWriter hardcodes UTF-16, and XmlWriter takes the DECLARED encoding from the writer — not from
        /// <see cref="XmlWriterSettings.Encoding"/> — so a plain StringWriter emits
        /// <c>&lt;?xml version="1.0" encoding="utf-16"?&gt;</c> on bytes that are then serialized as UTF-8. A
        /// conforming parser is entitled to reject that outright, and some e-readers do.
        /// </summary>
        public sealed class Utf8StringWriter : StringWriter
        {
            public Utf8StringWriter() : base(CultureInfo.InvariantCulture) { }
            public override Encoding Encoding => Encoding.UTF8;
        }

        /// <summary>The acquisition link's media type, by file extension. A client that trusts the declared type
        /// (Chunky, KyBook, Panels) refuses to open a book whose type is wrong, so guessing badly is worse than
        /// the generic fallback.</summary>
        public static string MediaTypeFor(string? extension) => (extension ?? "").ToLowerInvariant() switch
        {
            ".cbz" => "application/vnd.comicbook+zip",
            ".cbr" => "application/vnd.comicbook-rar",
            ".cb7" => "application/x-cb7",
            ".cbt" => "application/x-cbt",
            ".epub" => "application/epub+zip",
            ".pdf" => "application/pdf",
            ".mobi" => "application/x-mobipocket-ebook",
            ".azw" or ".azw3" => "application/vnd.amazon.ebook",
            _ => "application/octet-stream",
        };

        /// <summary>RFC 3339, which is what Atom's <c>updated</c> requires. Always UTC, always the same shape.</summary>
        public static string Timestamp(DateTime? value) =>
            DateTime.SpecifyKind(value ?? DateTime.UnixEpoch, DateTimeKind.Utc).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One Atom/OPDS document under construction. Wraps <see cref="XmlWriter"/> so the callers describe feeds and
    /// entries instead of elements, and so every document is opened and closed identically.
    /// </summary>
    public sealed class OpdsFeedWriter : IDisposable
    {
        private readonly OpdsXml.Utf8StringWriter sw = new();
        private readonly XmlWriter xw;
        private bool closed;

        public OpdsFeedWriter(string id, string title, string? subtitle = null, DateTime? updated = null)
        {
            xw = XmlWriter.Create(sw, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 });
            xw.WriteStartDocument();
            xw.WriteStartElement("feed", OpdsXml.AtomNs);
            xw.WriteAttributeString("xmlns", "opds", null, OpdsXml.OpdsNs);
            xw.WriteAttributeString("xmlns", "pse", null, OpdsXml.PseNs);
            xw.WriteAttributeString("xmlns", "dc", null, OpdsXml.DcNs);
            xw.WriteAttributeString("xmlns", "opensearch", null, OpdsXml.OpenSearchNs);
            xw.WriteElementString("id", OpdsXml.AtomNs, id);
            xw.WriteElementString("title", OpdsXml.AtomNs, title);
            if (!string.IsNullOrWhiteSpace(subtitle)) xw.WriteElementString("subtitle", OpdsXml.AtomNs, subtitle);
            xw.WriteElementString("updated", OpdsXml.AtomNs, OpdsXml.Timestamp(updated ?? DateTime.UtcNow));
        }

        /// <summary>OpenSearch paging facts. Readers that show "page 2 of 40" read these, not the next link.</summary>
        public void WritePaging(int totalResults, int itemsPerPage, int startIndex)
        {
            xw.WriteElementString("opensearch", "totalResults", OpdsXml.OpenSearchNs, totalResults.ToString(CultureInfo.InvariantCulture));
            xw.WriteElementString("opensearch", "itemsPerPage", OpdsXml.OpenSearchNs, itemsPerPage.ToString(CultureInfo.InvariantCulture));
            xw.WriteElementString("opensearch", "startIndex", OpdsXml.OpenSearchNs, startIndex.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// A link. <paramref name="pseCount"/>/<paramref name="pseLastRead"/> are what make a stream link a PSE
        /// stream link: without BOTH a <c>{pageNumber}</c> template in the href AND a <c>pse:count</c> in a
        /// declared namespace, every page-streaming client silently ignores it and downloads the archive.
        /// </summary>
        public void WriteLink(string rel, string type, string href, string? title = null,
            int? pseCount = null, int? pseLastRead = null, DateTime? pseLastReadDate = null)
        {
            xw.WriteStartElement("link", OpdsXml.AtomNs);
            xw.WriteAttributeString("rel", rel);
            xw.WriteAttributeString("type", type);
            xw.WriteAttributeString("href", href);
            if (title != null) xw.WriteAttributeString("title", title);
            if (pseCount is { } count) xw.WriteAttributeString("count", OpdsXml.PseNs, count.ToString(CultureInfo.InvariantCulture));
            if (pseLastRead is { } last) xw.WriteAttributeString("lastRead", OpdsXml.PseNs, last.ToString(CultureInfo.InvariantCulture));
            if (pseLastReadDate is { } when) xw.WriteAttributeString("lastReadDate", OpdsXml.PseNs, OpdsXml.Timestamp(when));
            xw.WriteEndElement();
        }

        public void StartEntry(string id, string title, DateTime? updated)
        {
            xw.WriteStartElement("entry", OpdsXml.AtomNs);
            xw.WriteElementString("id", OpdsXml.AtomNs, id);
            xw.WriteElementString("title", OpdsXml.AtomNs, title);
            xw.WriteElementString("updated", OpdsXml.AtomNs, OpdsXml.Timestamp(updated));
        }

        public void EndEntry() => xw.WriteEndElement();

        public void WriteAuthor(string name)
        {
            xw.WriteStartElement("author", OpdsXml.AtomNs);
            xw.WriteElementString("name", OpdsXml.AtomNs, name);
            xw.WriteEndElement();
        }

        public void WriteContent(string text, string type = "text")
        {
            xw.WriteStartElement("content", OpdsXml.AtomNs);
            xw.WriteAttributeString("type", type);
            xw.WriteString(text);
            xw.WriteEndElement();
        }

        public void WriteCategory(string term)
        {
            xw.WriteStartElement("category", OpdsXml.AtomNs);
            xw.WriteAttributeString("term", term);
            xw.WriteEndElement();
        }

        public void WriteDc(string name, string value) => xw.WriteElementString("dc", name, OpdsXml.DcNs, value);

        /// <summary>Close the document and hand back the XML. Idempotent — calling it twice returns the same string.</summary>
        public string Finish()
        {
            if (!closed)
            {
                xw.WriteEndElement();
                xw.WriteEndDocument();
                xw.Flush();
                closed = true;
            }
            return sw.ToString();
        }

        public void Dispose()
        {
            try { Finish(); } catch (InvalidOperationException) { /* a half-written document is still disposable */ }
            xw.Dispose();
            sw.Dispose();
        }
    }
}
