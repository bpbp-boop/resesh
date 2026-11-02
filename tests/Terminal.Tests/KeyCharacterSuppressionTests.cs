using Resesh.Terminal.Native;

namespace Resesh.Terminal.Tests;

/// <summary>Which text the hidden text box may send while an encoded key is held.</summary>
public class KeyCharacterSuppressionTests
{
    private const ushort KeyQ = 0x51, KeyA = 0x41;

    [Fact]
    public void NothingHeldSendsEverything()
    {
        Assert.Equal("hello", new KeyCharacterSuppression().Filter("hello"));
    }

    [Fact]
    public void DropsTheHeldKeysOwnCharacter()
    {
        var s = new KeyCharacterSuppression();
        s.Suppress(KeyQ, "@"); // Ctrl+left Alt+Q on a German layout
        Assert.Equal("", s.Filter("@"));
    }

    [Fact]
    public void OtherTextWhileTheKeyIsHeldStillGoesThrough()
    {
        var s = new KeyCharacterSuppression();
        s.Suppress(KeyQ, "@");
        Assert.Equal("dictated", s.Filter("@dictated"));
        Assert.Equal("😀", s.Filter("😀"));
    }

    [Fact]
    public void AKeyThatTypesNothingDropsNothing()
    {
        var s = new KeyCharacterSuppression();
        s.Suppress(0x25, ""); // Left arrow
        Assert.Equal("emoji", s.Filter("emoji"));
    }

    [Fact]
    public void AutoRepeatLeavesOneCopyPerRepeat()
    {
        var s = new KeyCharacterSuppression();
        s.Suppress(KeyA, "a");
        Assert.Equal("b", s.Filter("aaab"));
    }

    [Fact]
    public void UnknownCharactersDropEverythingWhileHeld()
    {
        var s = new KeyCharacterSuppression();
        s.Suppress(KeyA, null); // a dead key
        Assert.Equal("", s.Filter("anything"));
        s.KeyUp(KeyA);
        Assert.Equal("anything", s.Filter("anything"));
    }

    [Fact]
    public void KeyUpOfAnotherKeyKeepsSuppressing()
    {
        var s = new KeyCharacterSuppression();
        s.Suppress(KeyQ, null);
        s.KeyUp(KeyA);
        Assert.True(s.IsActive);
    }

    [Fact]
    public void AnotherKeyDownEndsAMissedKeyUp()
    {
        var s = new KeyCharacterSuppression();
        s.Suppress(KeyQ, null);
        s.KeyDown(KeyQ); // auto-repeat
        Assert.True(s.IsActive);
        s.KeyDown(KeyA);
        Assert.False(s.IsActive);
        Assert.Equal("text", s.Filter("text"));
    }
}
