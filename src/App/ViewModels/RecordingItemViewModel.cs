using System.Globalization;

namespace Resesh.App.ViewModels;

/// <summary>Lightweight metadata for one recording shown in the sessions rail.</summary>
public sealed class RecordingItemViewModel
{
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";

    public static RecordingItemViewModel FromFile(FileInfo file) => new()
    {
        FilePath = file.FullName,
        Name = Path.GetFileNameWithoutExtension(file.Name),
        Detail = $"{file.LastWriteTime.ToString("g", CultureInfo.CurrentCulture)}  •  {FormatSize(file.Length)}",
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes / (1024d * 1024d):0.#} MB",
    };
}
