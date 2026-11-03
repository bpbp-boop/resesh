using Resesh.Core.Models;

namespace Resesh.Core.Tests;

public sealed class SessionColorsTests
{
    [Theory]
    [InlineData("#0078D7")]
    [InlineData("#0078d7")]
    [InlineData(" #0078D7 ")]
    public void NamedTags_MatchWithoutRegardToCase(string saved)
    {
        var choices = SessionColors.ChoicesFor(saved);

        Assert.Same(SessionColors.All, choices);
        Assert.Equal("Blue", choices[SessionColors.IndexIn(choices, saved)].Name);
    }

    [Fact]
    public void AnUnknownTag_IsOfferedAsCustom_WithItsTextUnchanged()
    {
        var choices = SessionColors.ChoicesFor("#123abc");
        var index = SessionColors.IndexIn(choices, "#123abc");

        Assert.Equal(SessionColors.All.Count + 1, choices.Count);
        Assert.Equal("Custom (#123abc)", choices[index].Name);
        Assert.Equal("#123abc", choices[index].Hex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoTag_SelectsNone(string? saved)
    {
        var choices = SessionColors.ChoicesFor(saved);

        Assert.Same(SessionColors.None, choices[SessionColors.IndexIn(choices, saved)]);
    }

    [Fact]
    public void NamedTags_AreDistinctUppercaseRgb()
    {
        Assert.All(SessionColors.Named, choice => Assert.Matches("^#[0-9A-F]{6}$", choice.Hex));
        Assert.Equal(SessionColors.Named.Count, SessionColors.Named.Select(choice => choice.Hex).Distinct().Count());
    }
}
