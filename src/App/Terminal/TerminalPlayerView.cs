using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Models;
using Resesh.Core.Recording;
using Resesh.Core.Storage;
using Resesh.Terminal;

namespace Resesh.App.Terminal;

/// <summary>Read-only terminal player shared by live rewind and asciicast playback.</summary>
public sealed partial class TerminalPlayerView : Grid, IDisposable
{
    private readonly TerminalSurface _terminal = TerminalSurfaceFactory.CreatePlayback();
    private readonly TerminalCapture? _capture;
    private readonly TerminalRecording? _recording;
    private readonly Slider _timeline = new() { Minimum = 0, StepFrequency = 0.01 };
    private readonly TextBlock _time = new() { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _play = new() { Content = "Play", MinWidth = 64 };
    private readonly ComboBox _speed = new()
    {
        ItemsSource = new[] { 0.5, 1d, 2d, 4d },
        SelectedIndex = 1,
        Width = 76,
    };
    private readonly DispatcherQueueTimer _timer;
    private readonly LatestWinsRunner<double> _seeks;
    private bool _ready;
    private bool _initializeStarted;
    private bool _settingTimeline;
    private bool _disposed;
    private long _lastTick;

    // The overrides of the session whose output this plays (read live, so an edit to the
    // session restyles the player as it does the terminal); null for a recording file.
    private readonly Func<TerminalOverrides?> _overrides;
    private readonly Grid _controls = new() { Padding = new Thickness(10, 7, 10, 7), ColumnSpacing = 10 };

    /// <summary>Rewind of a live tab: drawn with the same effective settings as the tab's
    /// terminal (the session's overrides, and <paramref name="fontSize"/>, the terminal's
    /// zoomed size, when given).</summary>
    public TerminalPlayerView(TerminalCapture capture, Func<TerminalOverrides?> overrides, int? fontSize = null)
    {
        _capture = capture;
        _overrides = overrides;
        _timer = CreateTimer();
        _seeks = new LatestWinsRunner<double>(SeekAsync, ShowPlaybackError);
        Build("Live", fontSize);
    }

    public TerminalPlayerView(TerminalRecording recording)
    {
        _recording = recording;
        _overrides = () => null;
        _timer = CreateTimer();
        _seeks = new LatestWinsRunner<double>(SeekAsync, ShowPlaybackError);
        Build("Close", fontSize: null);
    }

    public event Action? CloseRequested;

    /// <summary>A window shortcut pressed while the player's terminal had focus.</summary>
    public event Action<string, int>? ShortcutRequested;

    private void Build(string closeLabel, int? fontSize)
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var appTheme = App.ResolveTheme(App.Settings.Current.Theme);
        var settings = App.Settings.Current.WithOverrides(_overrides());
        _terminal.SetInitialOptions(
            fontSize ?? settings.FontSize,
            settings.FontFamily,
            App.ResolveTheme(settings.Theme),
            copyOnSelect: true,
            rightClickPaste: false,
            scrollback: settings.Scrollback,
            readOnly: true);
        _terminal.ShortcutRequested += (id, chord) =>
            DispatcherQueue.TryEnqueue(() => ShortcutRequested?.Invoke(id, chord));
        _terminal.Ready += (_, _) =>
        {
            if (_ready || _disposed)
                return;
            _ready = true;
            _ = InitializeSourceSafelyAsync();
        };
        Children.Add(_terminal);

        var controls = _controls;
        controls.Background = new SolidColorBrush(ThemeVisualPalette.For(appTheme).InactiveTab);
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _play.Click += (_, _) => SetPlaying(_play.IsChecked == true);
        Grid.SetColumn(_play, 0);
        controls.Children.Add(_play);

        _speed.Header = null;
        ToolTipService.SetToolTip(_speed, "Playback speed");
        Grid.SetColumn(_speed, 1);
        controls.Children.Add(_speed);

        _timeline.ValueChanged += (_, _) =>
        {
            UpdateTimeLabel(_timeline.Value);
            if (!_settingTimeline)
                QueueSeek();
        };
        Grid.SetColumn(_timeline, 2);
        controls.Children.Add(_timeline);

        Grid.SetColumn(_time, 3);
        controls.Children.Add(_time);

        var state = new TextBlock
        {
            Text = _capture is null ? "Recording playback" : "Rewind snapshot — live output continues",
            Opacity = 0.68,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(state, 4);
        controls.Children.Add(state);

        var close = new Button { Content = closeLabel, MinWidth = 64 };
        close.Click += (_, _) => CloseRequested?.Invoke();
        Grid.SetColumn(close, 5);
        controls.Children.Add(close);

        Grid.SetRow(controls, 1);
        Children.Add(controls);

        // Loaded fires again each time the view is re-parented; the terminal is created once.
        Loaded += async (_, _) =>
        {
            if (_ready || _initializeStarted || _disposed)
                return;
            _initializeStarted = true;
            try
            {
                await _terminal.InitializeAsync();
            }
            catch (Exception exception)
            {
                _initializeStarted = false;
                ShowPlaybackError(exception);
            }
        };
    }

    private DispatcherQueueTimer CreateTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => AdvancePlayback();
        return timer;
    }

    private async Task InitializeSourceAsync()
    {
        if (_capture is not null)
        {
            var snapshot = _capture.Snapshot();
            SetTimelineBounds(snapshot.EarliestTime, snapshot.LatestTime, snapshot.LatestTime);
            _seeks.Request(snapshot.LatestTime);
        }
        else if (_recording is not null)
        {
            SetTimelineBounds(0, _recording.Duration, 0);
            var events = _recording.Events
                .Select(item => new TerminalTimedReplayEvent(item.Time, item.Type, item.Data))
                .ToArray();
            await _terminal.LoadPlaybackAsync(_recording.Width, _recording.Height, events);
        }
    }

    private async Task InitializeSourceSafelyAsync()
    {
        try
        {
            await InitializeSourceAsync();
        }
        catch (Exception exception)
        {
            ShowPlaybackError(exception);
        }
    }

    private void SetTimelineBounds(double minimum, double maximum, double value)
    {
        _settingTimeline = true;
        _timeline.Minimum = minimum;
        _timeline.Maximum = Math.Max(minimum, maximum);
        _timeline.Value = Math.Clamp(value, _timeline.Minimum, _timeline.Maximum);
        _settingTimeline = false;
        UpdateTimeLabel(_timeline.Value);
    }

    /// <summary>Seeks to the timeline position. One seek runs at a time; positions set while
    /// it runs collapse into the newest, which runs next.</summary>
    private void QueueSeek()
    {
        if (_ready && !_disposed)
            _seeks.Request(_timeline.Value);
    }

    private void ShowPlaybackError(Exception exception)
    {
        if (_disposed)
            return;
        SetPlaying(false);
        _time.Text = "Playback failed";
        ToolTipService.SetToolTip(_time, exception.Message);
    }

    private async Task SeekAsync(double time)
    {
        if (_disposed)
            return;
        if (_capture is not null)
        {
            var snapshot = _capture.Snapshot(time);
            var events = snapshot.Events
                .Select(item => new TerminalReplayEvent(item.Type, item.Data))
                .ToArray();
            await _terminal.ShowReplayAsync(
                snapshot.Keyframe?.Columns ?? snapshot.InitialColumns,
                snapshot.Keyframe?.Rows ?? snapshot.InitialRows,
                snapshot.Keyframe?.State ?? ReadOnlyMemory<byte>.Empty,
                events);
        }
        else
        {
            await _terminal.SeekPlaybackAsync(time);
        }
    }

    private void SetPlaying(bool playing)
    {
        _play.IsChecked = playing;
        _play.Content = playing ? "Pause" : "Play";
        if (playing)
        {
            _lastTick = Stopwatch.GetTimestamp();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void AdvancePlayback()
    {
        if (_disposed || _timeline.Value >= _timeline.Maximum)
        {
            SetPlaying(false);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;
        var speed = _speed.SelectedItem is double selected ? selected : 1d;
        _timeline.Value = Math.Min(_timeline.Maximum, _timeline.Value + elapsed * speed);
    }

    private void UpdateTimeLabel(double time)
    {
        if (_capture is not null)
        {
            var instant = _capture.StartedAt.AddSeconds(time).ToLocalTime();
            _time.Text = instant.ToString("yyyy-MM-dd HH:mm:ss");
        }
        else
        {
            _time.Text = Resesh.Core.History.ElapsedText.Clock(TimeSpan.FromSeconds(Math.Max(0, time)));
        }
    }

    /// <summary>Applies the app theme to the controls and the effective terminal theme (the
    /// session's override, if any) to the playback, as the live terminal does.</summary>
    public void ApplyTheme(string theme)
    {
        if (_disposed)
            return;
        theme = App.ResolveTheme(theme);
        _controls.Background = new SolidColorBrush(ThemeVisualPalette.For(theme).InactiveTab);
        _terminal.ApplyOptions(theme: App.ResolveTheme(_overrides()?.Theme ?? theme));
    }

    /// <summary>Applies font and scrollback settings, with the session's overrides on top.</summary>
    public void ApplySettings(AppSettings settings)
    {
        if (_disposed)
            return;
        var effective = settings.WithOverrides(_overrides());
        _terminal.ApplyOptions(
            fontSize: effective.FontSize,
            fontFamily: effective.FontFamily,
            scrollback: effective.Scrollback);
    }

    /// <summary>Stops the terminal drawing while this view is in the background or its
    /// window is minimized.</summary>
    public void SetRenderingPaused(bool paused)
    {
        if (!_disposed)
            _terminal.SetRenderingPaused(paused);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _seeks.Stop();
        _timer.Stop();
        _terminal.Dispose();
    }
}
