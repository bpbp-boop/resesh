namespace Resesh.Core.Storage;

/// <summary>An inclusive range of whole numbers a setting accepts.</summary>
public readonly record struct IntRange(int Minimum, int Maximum)
{
    public int Clamp(int value) => Math.Clamp(value, Minimum, Maximum);
}

/// <summary>
/// What the numeric settings accept, in one place: the Settings page, the session editors'
/// overrides and <see cref="AppSettings.WithOverrides"/> all read these, so a value typed in
/// one editor is never refused or clamped differently by another, or by a hand-edited file.
/// </summary>
public static class SettingRanges
{
    public static IntRange FontSize { get; } = new(8, 32);
    public static IntRange Scrollback { get; } = new(1000, 100000);
    public static IntRange CommandHistoryDays { get; } = new(1, 3650);
}
