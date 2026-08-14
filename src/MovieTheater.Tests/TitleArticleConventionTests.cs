using MovieTheater.Ingest;

namespace MovieTheater.Tests;

/// <summary>
/// The library's A-Z sort convention and its inverse. The inverse matters because the convention is
/// OURS: folder names carry "Sheep Detectives, The" while IMDb/OMDB/TMDB carry "The Sheep
/// Detectives", so a lookup that passes the folder spelling through gets a confident "not found"
/// rather than an error — the quietest way for an ingest to lose a title.
/// </summary>
public class TitleArticleConventionTests
{
    [Theory]
    [InlineData("Sheep Detectives, The", "The Sheep Detectives")]
    [InlineData("Ren & Stimpy Show, The", "The Ren & Stimpy Show")]
    [InlineData("Book of Mormon, The", "The Book of Mormon")]
    // The article re-attaches before the subtitle, not after it.
    [InlineData("Lord of the Rings, The: The Fellowship of the Ring", "The Lord of the Rings: The Fellowship of the Ring")]
    [InlineData("Thing, The: Remastered", "The Thing: Remastered")]
    // "A"/"An" are never produced by the inverter but hand-filed folders carry them.
    [InlineData("Fistful of Dollars, A", "A Fistful of Dollars")]
    [InlineData("American Werewolf in London, An", "An American Werewolf in London")]
    public void RestoreLeadingThe_puts_the_article_back(string sortForm, string natural) =>
        Assert.Equal(natural, TitleNorm.RestoreLeadingThe(sortForm));

    [Theory]
    [InlineData("Brick")]
    [InlineData("The Sheep Detectives")]        // already natural
    [InlineData("A Fistful of Dollars")]
    [InlineData("An American Werewolf in London")]
    [InlineData("Masters of the Universe")]     // interior "the" is not an article to move
    [InlineData("Tuner")]
    [InlineData("")]
    public void RestoreLeadingThe_leaves_everything_else_alone(string title) =>
        Assert.Equal(title, TitleNorm.RestoreLeadingThe(title));

    [Theory]
    [InlineData("The Sheep Detectives")]
    [InlineData("The Ren & Stimpy Show")]
    [InlineData("The Lord of the Rings: The Fellowship of the Ring")]
    [InlineData("The Thing: Remastered")]
    public void Invert_then_restore_is_the_identity(string natural) =>
        Assert.Equal(natural, TitleNorm.RestoreLeadingThe(TitleNorm.InvertLeadingThe(natural)));

    [Theory]
    [InlineData("Sheep Detectives, The")]
    [InlineData("Lord of the Rings, The: The Fellowship of the Ring")]
    public void Restore_then_invert_is_the_identity(string sortForm) =>
        Assert.Equal(sortForm, TitleNorm.InvertLeadingThe(TitleNorm.RestoreLeadingThe(sortForm)));

    [Fact]
    public void RestoreLeadingThe_is_null_safe() =>
        Assert.Null(TitleNorm.RestoreLeadingThe(null!));
}
