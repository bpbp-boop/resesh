using Microsoft.Win32;

namespace Resesh.App.Interop;

/// <summary>
/// Starting resesh when the user signs in, through the per-user Run key. Task Manager's
/// Startup apps page can turn the entry off without removing it, by marking it in
/// StartupApproved; that counts as off here too, and turning it on here clears the mark.
/// </summary>
internal static class SignInLaunch
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "resesh";

    public static bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string)
            return false;
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        // The first byte is even while the entry is enabled (2 or 6) and odd once disabled (3).
        return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || state[0] % 2 == 0;
    }

    /// <summary>Adds or removes the entry; false when the registry refused the change.</summary>
    public static bool TrySet(bool enabled, string executablePath)
    {
        try
        {
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                run.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
            else
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            TaskbarIntegration.TraceHook?.Invoke($"sign-in launch: {exception.Message}");
            return false;
        }
    }
}
