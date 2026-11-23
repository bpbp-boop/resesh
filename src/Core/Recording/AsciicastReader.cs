using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Resesh.Core.Recording;

public static class AsciicastReader
{
    private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    public static TerminalRecording Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("The cast file is empty.");
        using var header = ParseJson(headerLine, "header");
        var root = header.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var versionNumber) ||
            versionNumber != 2)
        {
            throw new InvalidDataException("Only asciicast v2 files are supported.");
        }

        var width = PositiveInt(root, "width", 80);
        var height = PositiveInt(root, "height", 24);
        var timestamp = ParseTimestamp(root, path);
        var title = ParseTitle(root);
        var events = new List<TerminalRecordingEvent>();
        string? line;
        var lineNumber = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using var document = ParseJson(line, $"event at line {lineNumber}");
            var item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3 ||
                item[0].ValueKind != JsonValueKind.Number || !item[0].TryGetDouble(out var time) || !double.IsFinite(time) ||
                item[1].ValueKind != JsonValueKind.String || item[1].GetString() is not ("o" or "r") ||
                item[2].ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"Invalid asciicast event at line {lineNumber}.");
            }
            var type = item[1].GetString()!;
            var data = item[2].GetString()!;
            if (time < 0 || (events.Count > 0 && time < events[^1].Time))
                throw new InvalidDataException($"Event times must be ordered at line {lineNumber}.");
            if (type == "r" && !TryParseSize(data, out _, out _))
                throw new InvalidDataException($"Invalid resize event at line {lineNumber}.");
            events.Add(new TerminalRecordingEvent(time, type, data));
        }
        return new TerminalRecording(timestamp, width, height, title, events);
    }

    public static bool TryParseSize(string value, out int columns, out int rows)
    {
        columns = 0;
        rows = 0;
        var separator = value.IndexOf('x');
        return separator > 0 && separator < value.Length - 1 &&
            int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out columns) &&
            int.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out rows) &&
            columns > 0 && rows > 0;
    }

    private static DateTimeOffset ParseTimestamp(JsonElement root, string path)
    {
        if (!root.TryGetProperty("timestamp", out var element) || element.ValueKind == JsonValueKind.Null)
            return File.GetCreationTimeUtc(path);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var unix)
            || unix < MinUnixSeconds || unix > MaxUnixSeconds)
        {
            throw new InvalidDataException("Invalid asciicast header timestamp.");
        }
        return DateTimeOffset.FromUnixTimeSeconds(unix);
    }

    private static string? ParseTitle(JsonElement root)
    {
        if (!root.TryGetProperty("title", out var element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid asciicast header title.");
        return element.GetString();
    }

    private static JsonDocument ParseJson(string value, string description)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid asciicast {description}.", exception);
        }
    }

    private static int PositiveInt(JsonElement root, string property, int fallback) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value) && value > 0
            ? value
            : fallback;
}
