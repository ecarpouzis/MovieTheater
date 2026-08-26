using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MovieTheater.Web
{
    /// <summary>
    /// The facets a board game can be grouped by, read out of the BGG links the sync stored as JSON
    /// (<c>BoardgameExtraDetails.LinksJson</c>: <c>[{ type, id, value, inbound }]</c>). Publisher and
    /// family are not columns on <c>Boardgame</c> — they only exist inside that array — so the
    /// boardgames catalog source asks for them once (<c>/API/Boardgames/Facets</c>) and groups the
    /// whole client-side catalog in memory, the way it already pages it. Inbound links (a family that
    /// points AT this game as its base) are skipped: they describe another item's relationship.
    /// </summary>
    public static class BoardgameLinkFacets
    {
        public sealed record Facets(List<string> Publishers, List<string> Families, List<string> Designers, List<string> Categories, List<string> Mechanics)
        {
            public static Facets Empty => new(new(), new(), new(), new(), new());
        }

        public static Facets Parse(string? linksJson)
        {
            var f = Facets.Empty;
            if (string.IsNullOrWhiteSpace(linksJson)) return f;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(linksJson); }
            catch (JsonException) { return f; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return f;
                foreach (var link in doc.RootElement.EnumerateArray())
                {
                    if (link.ValueKind != JsonValueKind.Object) continue;
                    var type = Str(link, "type");
                    var value = Str(link, "value");
                    if (type == null || string.IsNullOrWhiteSpace(value)) continue;
                    if (Bool(link, "inbound")) continue;
                    var list = type.ToLowerInvariant() switch
                    {
                        "boardgamepublisher" => f.Publishers,
                        "boardgamefamily" => f.Families,
                        "boardgamedesigner" => f.Designers,
                        "boardgamecategory" => f.Categories,
                        "boardgamemechanic" => f.Mechanics,
                        _ => null,
                    };
                    if (list != null && !list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value.Trim());
                }
            }
            return f;
        }

        private static string? Str(JsonElement obj, string name)
        {
            foreach (var p in obj.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
            return null;
        }

        private static bool Bool(JsonElement obj, string name)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == JsonValueKind.True) return true;
                if (p.Value.ValueKind == JsonValueKind.String && bool.TryParse(p.Value.GetString(), out var b)) return b;
            }
            return false;
        }
    }
}
