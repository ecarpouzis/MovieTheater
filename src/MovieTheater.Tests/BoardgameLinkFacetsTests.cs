using MovieTheater.Web;
using Xunit;

namespace MovieTheater.Tests
{
    public class BoardgameLinkFacetsTests
    {
        [Fact]
        public void Parses_the_stored_link_array_by_type_and_skips_inbound_links()
        {
            const string json = """
            [
              {"type":"boardgamepublisher","id":1,"value":"Fantasy Flight Games","inbound":false},
              {"type":"boardgamePublisher","id":2,"value":"Fantasy Flight Games","inbound":false},
              {"type":"boardgamefamily","id":3,"value":"Series: Arkham Horror","inbound":false},
              {"type":"boardgamefamily","id":4,"value":"Points at me","inbound":true},
              {"type":"boardgamedesigner","id":5,"value":"Richard Launius"},
              {"type":"boardgamecategory","id":6,"value":"Horror"},
              {"type":"boardgamemechanic","id":7,"value":"Dice Rolling"},
              {"type":"boardgameexpansion","id":8,"value":"Some Expansion"},
              {"type":"boardgamemechanic","id":9,"value":"   "}
            ]
            """;
            var f = BoardgameLinkFacets.Parse(json);
            Assert.Equal(new[] { "Fantasy Flight Games" }, f.Publishers);
            Assert.Equal(new[] { "Series: Arkham Horror" }, f.Families);
            Assert.Equal(new[] { "Richard Launius" }, f.Designers);
            Assert.Equal(new[] { "Horror" }, f.Categories);
            Assert.Equal(new[] { "Dice Rolling" }, f.Mechanics);
        }

        [Fact]
        public void Tolerates_missing_or_broken_json()
        {
            Assert.Empty(BoardgameLinkFacets.Parse(null).Publishers);
            Assert.Empty(BoardgameLinkFacets.Parse("").Families);
            Assert.Empty(BoardgameLinkFacets.Parse("{not json").Publishers);
            Assert.Empty(BoardgameLinkFacets.Parse("{\"type\":\"boardgamepublisher\"}").Publishers);
        }
    }
}
