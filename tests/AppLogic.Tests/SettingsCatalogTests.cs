using System.Reflection;
using Resesh.App.ViewModels;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

/// <summary>Adding a setting takes a row in <see cref="SettingsCatalog"/>: these fail until it
/// has one (or a reason it has no field), so a setting can't be left out of the palette or
/// unreachable from a command. SettingsPageTests.js checks the page has a field for each row.</summary>
public sealed class SettingsCatalogTests
{
    [Fact]
    public void EveryTargetHasOneEntry()
    {
        Assert.Equal(
            Enum.GetValues<GlobalSettingsTarget>().Order(),
            SettingsCatalog.Targets.Select(entry => entry.Target).Order());
    }

    [Fact]
    public void EverySavedSettingHasATargetOrAReasonItHasNone()
    {
        var properties = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is not null)
            .Select(property => property.Name)
            .ToHashSet();
        var mapped = SettingsCatalog.Targets.Select(entry => entry.Setting).OfType<string>().ToHashSet();
        var exempt = SettingsCatalog.NotInSettings.Keys.ToHashSet();

        Assert.Empty(properties.Except(mapped).Except(exempt));
        Assert.Empty(mapped.Except(properties));
        Assert.Empty(exempt.Except(properties));
        Assert.Empty(mapped.Intersect(exempt));
        Assert.All(SettingsCatalog.NotInSettings.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void ATargetNamedAfterASectionOpensThatSection()
    {
        foreach (var entry in SettingsCatalog.Targets)
        {
            if (Enum.TryParse<SettingsSection>(entry.Target.ToString(), out var section))
            {
                Assert.Equal(section, entry.Section);
                Assert.Null(entry.Setting);
            }
        }
    }

    [Fact]
    public void PaletteTitlesAreDistinct()
    {
        var titles = SettingsCatalog.Targets.Select(entry => entry.PaletteTitle).ToList();
        Assert.Equal(titles.Count, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void PathsNameASectionWithOneSeparator()
    {
        Assert.Equal("Settings › Identities & Keys", SettingsCatalog.PathTo(SettingsSection.Identities));
        Assert.Equal("Settings › Identities & Keys › SSH algorithms", SettingsCatalog.PathTo(SettingsSection.Identities, "SSH algorithms"));
        // The multi-line paste prompt points at the card's real section.
        Assert.Equal("Settings › General", SettingsCatalog.PathTo(GlobalSettingsTarget.ConfirmMultilinePaste));
    }
}
