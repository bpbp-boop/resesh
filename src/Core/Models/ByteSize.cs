namespace Resesh.Core.Models;

public static class ByteSize
{
    /// <summary>Human-readable size in binary units: "512 B", "1.5 KB", "3.2 MB", "2.05 GB".</summary>
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
