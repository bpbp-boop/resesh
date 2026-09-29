using System.Globalization;
using Resesh.Core.Models;

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
        Detail = $"{file.LastWriteTime.ToString("g", CultureInfo.CurrentCulture)}  •  {ByteSize.Format(file.Length)}",
    };
}
