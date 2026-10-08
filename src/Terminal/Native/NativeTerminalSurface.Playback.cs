using System.Text;

namespace Resesh.Terminal.Native;

// Async playback code lives outside the unsafe part of the class (await is not allowed in an
// unsafe context); pointer work stays in NativeTerminalSurface.cs.
public sealed partial class NativeTerminalSurface
{
    // ---- rewind and recording playback ----------------------------------------------------

    /// <summary>Rewind: a keyframe (VT text, as <see cref="KeyframeCaptured"/> produces) and
    /// the output after it, replayed into a reset terminal of the recorded size.</summary>
    public override async Task ShowReplayAsync(int columns, int rows, ReadOnlyMemory<byte> keyframe,
        IReadOnlyList<TerminalReplayEvent> events)
    {
        var generation = ++_replayGeneration;
        var state = keyframe.ToArray();
        var items = events.ToArray();
        await RunReplayAsync(generation, () =>
        {
            var size = ResetLocked(columns, rows);
            if (state.Length > 0)
                WriteLocked(state);
            foreach (var item in items)
                size = ApplyEventLocked(item.Type, item.Data, size);
            return size;
        });
    }

    /// <summary>A whole asciicast recording: replayed once to build seek keyframes every 10 s
    /// or 1 MiB of output, then shown at the pending seek position.</summary>
    public override async Task LoadPlaybackAsync(int columns, int rows, IReadOnlyList<TerminalTimedReplayEvent> events)
    {
        var generation = ++_replayGeneration;
        _playback = null;
        var items = events.ToArray();
        var frames = new List<PlaybackFrame> { new(0, 0, Math.Max(1, columns), Math.Max(1, rows), []) };
        await RunReplayAsync(generation, () =>
        {
            var size = ResetLocked(columns, rows);
            double lastFrameTime = 0;
            long bytesSinceFrame = 0;
            for (var index = 0; index < items.Length; index++)
            {
                if (generation != _replayGeneration)
                    return size;
                var item = items[index];
                size = ApplyEventLocked(item.Type, item.Data, size);
                if (item.Type != "o")
                    continue;
                bytesSinceFrame += item.Data.Length;
                if (item.Time - lastFrameTime >= 10 || bytesSinceFrame >= 1024 * 1024)
                {
                    frames.Add(new PlaybackFrame(item.Time, index + 1, size.Columns, size.Rows, FormatVtLocked()));
                    lastFrameTime = item.Time;
                    bytesSinceFrame = 0;
                }
            }
            return size;
        });
        if (generation != _replayGeneration)
            return;
        _playback = new PlaybackModel(items, frames);
        _playbackPosition = -1;
        await SeekPlaybackAsync(_pendingPlaybackSeek);
    }

    /// <summary>Shows the recording at <paramref name="time"/>. Moving forward writes only the
    /// events since the current position; moving back restarts from the nearest keyframe.</summary>
    public override async Task SeekPlaybackAsync(double time)
    {
        _pendingPlaybackSeek = Math.Max(0, time);
        if (_playback is not { } playback)
            return;
        var generation = ++_replayGeneration;
        var target = _pendingPlaybackSeek;
        await RunReplayAsync(generation, () =>
        {
            (int Columns, int Rows) size;
            int index;
            if (_playbackPosition >= 0 && target >= _playbackPosition && _fixedGrid is { } current)
            {
                size = current;
                index = _playbackIndex;
            }
            else
            {
                var frame = playback.Frames[0];
                for (var i = playback.Frames.Count - 1; i >= 0; i--)
                {
                    if (playback.Frames[i].Time <= target)
                    {
                        frame = playback.Frames[i];
                        break;
                    }
                }
                size = ResetLocked(frame.Columns, frame.Rows);
                if (frame.State.Length > 0)
                    WriteLocked(frame.State);
                index = frame.Index;
            }
            while (index < playback.Events.Length && playback.Events[index].Time <= target)
            {
                var item = playback.Events[index++];
                size = ApplyEventLocked(item.Type, item.Data, size);
            }
            _playbackIndex = index;
            _playbackPosition = target;
            return size;
        }, resets: () => !(_playbackPosition >= 0 && target >= _playbackPosition && _fixedGrid is not null));
    }

    /// <summary>Runs one replay off the UI thread, newest request wins: an older replay that
    /// has not started is skipped. Rendering pauses until the replay settles, then repaints.
    /// A replay that resets the terminal starts a fresh command tracker first, so the OSC 133
    /// marks in the recording land in it (playback views do not listen to its events); one
    /// that only writes on (a forward seek) keeps the marks it has.</summary>
    private async Task RunReplayAsync(int generation, Func<(int Columns, int Rows)> replay, Func<bool>? resets = null)
    {
        if (_disposed)
            return;
        if (!_initialized)
            await InitializeAsync();
        await _replayGate.WaitAsync();
        try
        {
            if (generation != _replayGeneration || _disposed)
                return;
            // Decided under the gate: only replays move the playback position.
            if ((resets?.Invoke() ?? true) || _commands is null)
            {
                _commands?.Dispose();
                _commands = null; // marks from before the replay describe other content
                CreateCommandTracker();
            }
            _replaying = true;
            var size = await Task.Run(() =>
            {
                lock (_termGate)
                    return _term == IntPtr.Zero ? (Columns, Rows) : replay();
            });
            if (_disposed)
                return;
            _fixedGrid = size;
            _replaying = false;
            _forceFull = true;
            Relayout();
            RequestFrame();
        }
        finally
        {
            _replaying = false;
            _replayGate.Release();
        }
    }

    private (int Columns, int Rows) ResetLocked(int columns, int rows)
    {
        GhosttyNative.rvt_reset(_term);
        var size = (Math.Max(1, columns), Math.Max(1, rows));
        ResizeLocked(size);
        _playbackPosition = -1;
        return size;
    }

    private void ResizeLocked((int Columns, int Rows) size) =>
        GhosttyNative.rvt_resize(_term, (ushort)Math.Min(size.Columns, ushort.MaxValue), (ushort)Math.Min(size.Rows, ushort.MaxValue),
            (uint)Math.Max(1, _renderer.CellWidth), (uint)Math.Max(1, _renderer.CellHeight));

    /// <summary>One asciicast event: "o" output (UTF-8 text), "r" a "COLSxROWS" resize.</summary>
    private (int Columns, int Rows) ApplyEventLocked(string type, string data, (int Columns, int Rows) size)
    {
        if (type == "o")
        {
            WriteLocked(Encoding.UTF8.GetBytes(data));
        }
        else if (type == "r")
        {
            var x = data.IndexOf('x');
            if (x > 0 && int.TryParse(data.AsSpan(0, x), out var c) && int.TryParse(data.AsSpan(x + 1), out var r))
            {
                size = (Math.Max(1, c), Math.Max(1, r));
                ResizeLocked(size);
            }
        }
        return size;
    }
}
