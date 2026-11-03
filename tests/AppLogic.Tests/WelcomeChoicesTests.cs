using Resesh.App.ViewModels;
using Resesh.Core.Storage;

namespace Resesh.AppLogic.Tests;

public sealed class WelcomeChoicesTests
{
    private static readonly AppSettings Fresh = new() { OnboardingCompleted = false, Theme = "dark" };

    [Fact]
    public void Finish_KeepsAThemeSavedInSettingsWhileWelcomeWasOpen()
    {
        var choices = new WelcomeChoices(Fresh);
        choices.Choose(choices.Shown with { CopyOnSelect = false });
        var saved = Fresh with { Theme = "nord", ShowStatusBar = false };

        var finished = choices.Complete(saved);

        Assert.Equal("nord", finished.Theme);
        Assert.False(finished.ShowStatusBar);
        Assert.False(finished.CopyOnSelect);
        Assert.True(finished.OnboardingCompleted);
    }

    [Fact]
    public void ASettingSavedElsewhere_ReplacesThePickMadeOverIt_AndKeepsTheOthers()
    {
        var choices = new WelcomeChoices(Fresh);
        choices.Choose(choices.Shown with { Theme = "light", RightClickPaste = false });
        Assert.True(choices.PreviewsTheme);

        choices.Refresh(Fresh with { Theme = "dracula" });

        Assert.Equal("dracula", choices.Shown.Theme);
        Assert.False(choices.PreviewsTheme);
        Assert.False(choices.Shown.RightClickPaste);
    }

    [Fact]
    public void Refresh_ShowsSettingsTheUserDidNotTouch()
    {
        var choices = new WelcomeChoices(Fresh);

        choices.Refresh(Fresh with { KeepCommandHistory = true, ConfirmCloseActiveSessions = false });

        Assert.True(choices.Shown.KeepCommandHistory);
        Assert.False(choices.Shown.ConfirmCloseActiveSessions);
    }

    [Fact]
    public void Finish_IsOffOnlyOnceWelcomeIsDoneAndNothingDiffers()
    {
        var done = Fresh with { OnboardingCompleted = true };
        var choices = new WelcomeChoices(done);
        Assert.True(choices.HasChanges(Fresh));
        Assert.False(choices.HasChanges(done));

        choices.Choose(choices.Shown with { Theme = "DARK" });
        Assert.False(choices.HasChanges(done));
        choices.Choose(choices.Shown with { WriteCrashReports = false });
        Assert.True(choices.HasChanges(done));
    }
}
