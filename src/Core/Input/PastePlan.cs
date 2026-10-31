using System.Globalization;
using System.Text;

namespace Resesh.Core.Input;

/// <summary>One piece of a paced paste: bytes to send, then how long to wait.</summary>
public readonly record struct PasteChunk(byte[] Bytes, TimeSpan DelayAfter, bool EndsLine);

/// <summary>
/// Decides what a paste does before it is sent: whether it would run commands (so the user
/// is asked first), and how to send it slowly to a device that drops characters from a
/// fast paste. Line breaks of any style become CR, the Enter key, which is what the terminal
/// sends for an unbracketed paste too.
/// </summary>
public static class PastePlan
{
    /// <summary>The paste with every line break as LF.</summary>
    public static string Normalize(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    /// <summary>How many Enters the paste presses.</summary>
    public static int LineBreaks(string text) => Normalize(text).Count(c => c == '\n');

    /// <summary>A paste asks first when it would press Enter: unless the program turned on
    /// bracketed paste (then it decides what a pasted line break does), each line runs.</summary>
    public static bool NeedsConfirmation(string text, bool bracketedPaste) =>
        !bracketedPaste && LineBreaks(text) > 0;

    /// <summary>The first lines, for the confirmation, with a count of the rest.</summary>
    public static string Preview(string text, int maxLines = 6, int maxWidth = 100)
    {
        var lines = Normalize(text).TrimEnd('\n').Split('\n');
        var shown = lines.Take(maxLines)
            .Select(line => line.Length > maxWidth ? line[..maxWidth] + "…" : line);
        var preview = string.Join('\n', shown);
        return lines.Length > maxLines ? $"{preview}\n… and {lines.Length - maxLines} more" : preview;
    }

    /// <summary>True when either delay is set, so the paste is sent by <see cref="Chunks"/>
    /// instead of all at once.</summary>
    public static bool IsPaced(int lineDelayMs, int characterDelayMs) => lineDelayMs > 0 || characterDelayMs > 0;

    /// <summary>The paste as chunks to send in order: whole lines when only the line delay
    /// is set, single characters (one Unicode text element each) when the character delay
    /// is. A line's Enter is its last chunk and carries the line delay.</summary>
    public static IReadOnlyList<PasteChunk> Chunks(string text, int lineDelayMs, int characterDelayMs)
    {
        var lineDelay = TimeSpan.FromMilliseconds(Math.Max(0, lineDelayMs));
        var characterDelay = TimeSpan.FromMilliseconds(Math.Max(0, characterDelayMs));
        var chunks = new List<PasteChunk>();
        var lines = Normalize(text).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var endsLine = index < lines.Length - 1;
            if (characterDelay > TimeSpan.Zero)
            {
                var elements = StringInfo.GetTextElementEnumerator(line);
                while (elements.MoveNext())
                    chunks.Add(new PasteChunk(Encoding.UTF8.GetBytes(elements.GetTextElement()), characterDelay, false));
                if (endsLine)
                    chunks.Add(new PasteChunk([(byte)'\r'], lineDelay > characterDelay ? lineDelay : characterDelay, true));
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(endsLine ? line + "\r" : line);
                if (bytes.Length > 0)
                    chunks.Add(new PasteChunk(bytes, endsLine ? lineDelay : TimeSpan.Zero, endsLine));
            }
        }
        return chunks;
    }

    /// <summary>Sends <paramref name="chunks"/> through <paramref name="write"/>, waiting
    /// after each. Reports each finished line. Stops quietly when cancelled. Every call to
    /// <paramref name="write"/> runs on the caller's context, because callers write to UI
    /// state; so the waits must not use ConfigureAwait(false).</summary>
    public static async Task SendAsync(IReadOnlyList<PasteChunk> chunks, Action<byte[]> write,
        IProgress<int>? linesSent = null, CancellationToken cancellationToken = default,
        TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var lines = 0;
        foreach (var chunk in chunks)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            write(chunk.Bytes);
            if (chunk.EndsLine)
                linesSent?.Report(++lines);
            if (chunk.DelayAfter > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(chunk.DelayAfter, time, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
