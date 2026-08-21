using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Recording;
using Resesh.Terminal;

namespace Resesh.App.Terminal;

/// <summary>Read-only xterm player shared by live rewind and asciicast playback.</summary>
public sealed class TerminalPlayerView : Grid, IDisposable
{
    private readonly TerminalControl _terminal = new();
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
    private bool _ready;
    private bool _seekQueued;
    private bool _settingTimeline;
    private bool _disposed;
    private long _lastTick;

    public TerminalPlayerView(TerminalCapture capture)
    {
        _capture = capture;
        _timer = CreateTimer();
        Build("Live");
    }

    public TerminalPlayerView(TerminalRecording recording)
    {
        _recording = recording;
        _timer = CreateTimer();
        Build("Close");
    }

    public event Action? CloseRequested;

    private void Build(string closeLabel)
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var settings = App.Settings.Current;
        _terminal.SetInitialOptions(
            settings.FontSize,
            settings.FontFamily,
            settings.Theme,
            copyOnSelect: true,
            rightClickPaste: false,
            scrollback: settings.Scrollback,
            readOnly: true);
        _terminal.Ready += (_, _) =>
        {
            _ready = true;
            InitializeSource();
        };
        Children.Add(_terminal);

        var controls = new Grid
        {
            Padding = new Thickness(10, 7, 10, 7),
            ColumnSpacing = 10,
            Background = new SolidColorBrush(ThemeVisualPalette.For(settings.Theme).InactiveTab),
        };
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

        Loaded += async (_, _) =>
        {
            if (!_ready)
                await _terminal.InitializeAsync();
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

    private void InitializeSource()
    {
        if (_capture is not null)
        {
            var snapshot = _capture.Snapshot();
            SetTimelineBounds(snapshot.EarliestTime, snapshot.LatestTime, snapshot.LatestTime);
            Seek(snapshot.LatestTime);
        }
        else if (_recording is not null)
        {
            SetTimelineBounds(0, _recording.Duration, 0);
            _terminal.LoadPlayback(
                _recording.Width,
                _recording.Height,
                _recording.Events
                    .Select(item => new TerminalTimedReplayEvent(item.Time, item.Type, item.Data))
                    .ToArray());
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

    private void QueueSeek()
    {
        if (!_ready || _seekQueued)
            return;
        _seekQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _seekQueued = false;
            Seek(_timeline.Value);
        });
    }

    private void Seek(double time)
    {
        if (_capture is not null)
        {
            var snapshot = _capture.Snapshot(time);
            _terminal.ShowReplay(
                snapshot.Keyframe?.Columns ?? snapshot.InitialColumns,
                snapshot.Keyframe?.Rows ?? snapshot.InitialRows,
                snapshot.Keyframe?.State,
                snapshot.Events.Select(item => new TerminalReplayEvent(item.Type, item.Data)).ToArray());
        }
        else
        {
            _terminal.SeekPlayback(time);
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
            var elapsed = TimeSpan.FromSeconds(Math.Max(0, time));
            _time.Text = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"hh\:mm\:ss\.fff")
                : elapsed.ToString(@"mm\:ss\.fff");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _terminal.Dispose();
    }
}
