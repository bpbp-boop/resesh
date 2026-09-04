using System.Text.Json;

namespace Resesh.Terminal;

public sealed record NativeTerminalKeyframe(
    int SchemaVersion,
    string BuildId,
    int Columns,
    int Rows,
    int CursorColumn,
    int CursorRow,
    int ViewportTop,
    int ViewportHeight,
    int ScrollOffset,
    bool CursorVisible,
    bool AlternateBuffer,
    ulong CaptureSequence,
    long UnixTimeMilliseconds,
    string Title,
    string WorkingDirectory,
    string Ansi);

public static class NativeTerminalKeyframeCodec
{
    private const string Prefix = "resesh-native-keyframe-v1:";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string Encode(NativeTerminalApi.Snapshot snapshot, string buildId) =>
        Prefix + JsonSerializer.Serialize(new NativeTerminalKeyframe(
            checked((int)snapshot.SchemaVersion),
            buildId,
            snapshot.Columns,
            snapshot.Rows,
            snapshot.CursorColumn,
            snapshot.CursorRow,
            snapshot.ViewportTop,
            snapshot.ViewportHeight,
            snapshot.ScrollOffset,
            snapshot.CursorVisible,
            snapshot.AlternateBuffer,
            snapshot.CaptureSequence,
            snapshot.UnixTimeMilliseconds,
            snapshot.Title,
            snapshot.WorkingDirectory,
            snapshot.Ansi), Options);

    public static bool TryDecode(string? state, out NativeTerminalKeyframe? keyframe)
    {
        keyframe = null;
        if (state is null || !state.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        try
        {
            var decoded = JsonSerializer.Deserialize<NativeTerminalKeyframe>(state.AsSpan(Prefix.Length), Options);
            if (decoded is null
                || decoded.SchemaVersion != 1
                || string.IsNullOrWhiteSpace(decoded.BuildId)
                || decoded.Columns <= 0
                || decoded.Rows <= 0
                || decoded.ViewportHeight <= 0
                || decoded.ViewportHeight != decoded.Rows
                || decoded.CursorColumn < 0
                || decoded.CursorColumn >= decoded.Columns
                || decoded.CursorRow < 0
                || decoded.CursorRow >= decoded.ViewportHeight
                || decoded.ViewportTop < 0
                || decoded.ScrollOffset < 0
                || decoded.UnixTimeMilliseconds < 0
                || decoded.BuildId.Length > 256
                || decoded.Title is null
                || decoded.Title.Length > 4096
                || decoded.WorkingDirectory is null
                || decoded.WorkingDirectory.Length > 16384
                || decoded.Ansi is null
                || decoded.Ansi.Length > 32 * 1024 * 1024)
            {
                return false;
            }
            keyframe = decoded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
