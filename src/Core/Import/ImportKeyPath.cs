using Resesh.Core.Storage;

namespace Resesh.Core.Import;

internal static class ImportKeyPath
{
    /// <summary>An imported key path as <see cref="KeyFilePath"/> reads it; "none" (OpenSSH's
    /// way of saying no key) and blank text mean no key.</summary>
    public static string? Resolve(string? value, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;
        return KeyFilePath.Normalize(value, baseDirectory);
    }
}
