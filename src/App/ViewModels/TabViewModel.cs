using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Sessions.Core.Models;

namespace Sessions.App.ViewModels;

public enum TabConnectionState
{
    Connecting,
    Connected,
    Disconnected,
}

/// <summary>One open tab: a session plus its terminal view and connection state.</summary>
public sealed class TabViewModel : ObservableObject
{
    private Session _session;
    private string? _titleOverride;
    private TabConnectionState _state = TabConnectionState.Connecting;
    private string _connectionSummary = "";
    private object? _view;

    public TabViewModel(Session session)
    {
        _session = session;
    }

    /// <summary>
    /// Distinguishes this tab's tmux session when several tabs of the same saved session are
    /// open (Clone): slot 0 is the primary; clones get their own remote session. Fixed at
    /// tab creation so Reconnect re-attaches to the same remote session.
    /// </summary>
    public int TmuxSlot { get; set; }

    public Session Session
    {
        get => _session;
        set
        {
            if (SetProperty(ref _session, value))
            {
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(Endpoint));
                OnPropertyChanged(nameof(IconSource));
                OnPropertyChanged(nameof(IconVisibility));
            }
        }
    }

    /// <summary>The tab's content control (TerminalTabView); set by the window after creation.</summary>
    public object? View
    {
        get => _view;
        set => SetProperty(ref _view, value);
    }

    /// <summary>Display-only tab title override (context-menu Rename, M4). Null = session name.</summary>
    public string? TitleOverride
    {
        get => _titleOverride;
        set
        {
            if (SetProperty(ref _titleOverride, value))
                OnPropertyChanged(nameof(Header));
        }
    }

    public TabConnectionState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateColor));
            }
        }
    }

    // ---- VS Code-style tab visuals (the header content paints the whole tab) ----

    private bool _isActive;
    private bool _isPointerOver;

    /// <summary>Whether this tab is its group's selected tab; drives the tab visuals.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
                NotifyTabVisuals();
        }
    }

    /// <summary>Pointer hover, set by the view; reveals the close × and hover tint (VS Code style).</summary>
    public bool IsPointerOver
    {
        get => _isPointerOver;
        set
        {
            if (SetProperty(ref _isPointerOver, value))
                NotifyTabVisuals();
        }
    }

    private void NotifyTabVisuals()
    {
        OnPropertyChanged(nameof(AccentVisibility));
        OnPropertyChanged(nameof(HeaderBackground));
        OnPropertyChanged(nameof(HeaderForeground));
        OnPropertyChanged(nameof(CloseOpacity));
        OnPropertyChanged(nameof(CloseInteractive));
    }

    public Visibility AccentVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The × shows on the active tab and on hover; hidden (but space kept) otherwise. Never on pinned tabs.</summary>
    public double CloseOpacity => !IsPinned && (IsActive || IsPointerOver) ? 1.0 : 0.0;

    public bool CloseInteractive => !IsPinned && (IsActive || IsPointerOver);

    private static bool IsDark =>
        Microsoft.UI.Xaml.Application.Current.RequestedTheme == Microsoft.UI.Xaml.ApplicationTheme.Dark;

    public Microsoft.UI.Xaml.Media.Brush HeaderBackground
    {
        get
        {
            // VS Code Dark Modern: inactive tabs share the strip background; the active
            // tab matches the editor (here: the terminal); hover gets a subtle tint.
            Windows.UI.Color color;
            if (IsActive)
                color = IsDark ? Windows.UI.Color.FromArgb(255, 0x0C, 0x0C, 0x0C) : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF);
            else if (IsPointerOver)
                color = IsDark ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20) : Windows.UI.Color.FromArgb(255, 0xE8, 0xE8, 0xE8);
            else
                color = IsDark ? Windows.UI.Color.FromArgb(255, 0x18, 0x18, 0x18) : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        }
    }

    public Microsoft.UI.Xaml.Media.Brush HeaderForeground => new Microsoft.UI.Xaml.Media.SolidColorBrush(
        IsActive
            ? (IsDark ? Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(255, 0x33, 0x33, 0x33))
            : (IsDark ? Windows.UI.Color.FromArgb(255, 0x9D, 0x9D, 0x9D) : Windows.UI.Color.FromArgb(255, 0x61, 0x61, 0x61)));

    /// <summary>Tab-strip status dot: green connected, red disconnected, amber connecting.</summary>
    public Windows.UI.Color StateColor => State switch
    {
        TabConnectionState.Connected => Windows.UI.Color.FromArgb(255, 0x16, 0xC6, 0x0C),
        TabConnectionState.Connecting => Windows.UI.Color.FromArgb(255, 0xFF, 0xB9, 0x00),
        _ => Windows.UI.Color.FromArgb(255, 0xE7, 0x48, 0x56),
    };

    /// <summary>e.g. "aes256-gcm@openssh.com • SHA256:…" once connected.</summary>
    public string ConnectionSummary
    {
        get => _connectionSummary;
        set => SetProperty(ref _connectionSummary, value);
    }

    public string Header => TitleOverride ?? Session.Name;

    /// <summary>Session icon for the tab strip; null collapses the Image.</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? IconSource => App.Icons.GetImage(Session.Icon);

    public Visibility IconVisibility => IconSource is null ? Visibility.Collapsed : Visibility.Visible;

    public string Endpoint => $"{Session.Username}@{Session.Host}:{Session.Port}";

    public string StateText => State switch
    {
        TabConnectionState.Connecting => "connecting…",
        TabConnectionState.Connected => "connected",
        _ => "disconnected",
    };

    // ---- Pin state (browser-style: pinned tabs can't be closed without unpinning) ----

    private bool _isPinned;

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(PinIconVisibility));
                NotifyTabVisuals(); // the × hides while pinned
            }
        }
    }

    public Visibility PinIconVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    // ---- Lock state (per plan: password held in memory only; never persisted) ----

    private bool _isLocked;
    private string? _lockPassword;

    public bool IsLocked
    {
        get => _isLocked;
        private set
        {
            if (SetProperty(ref _isLocked, value))
                OnPropertyChanged(nameof(LockIconVisibility));
        }
    }

    public Visibility LockIconVisibility => IsLocked ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Session color tag for the tab strip; transparent when unset.</summary>
    public Windows.UI.Color TagColor
    {
        get
        {
            var hex = Session.ColorTag;
            if (hex is { Length: 7 } && hex[0] == '#'
                && byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
                && byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                && byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                return Windows.UI.Color.FromArgb(255, r, g, b);
            }
            return Windows.UI.Color.FromArgb(0, 0, 0, 0);
        }
    }

    /// <summary>Set while locked-out after repeated failed unlock attempts.</summary>
    public DateTimeOffset LockoutUntil { get; private set; } = DateTimeOffset.MinValue;

    private int _failedUnlockAttempts;

    public void Lock(string password)
    {
        _lockPassword = password;
        _failedUnlockAttempts = 0;
        IsLocked = true;
    }

    /// <summary>False on wrong password; three misses start a 30-second lockout.</summary>
    public bool TryUnlock(string password)
    {
        if (DateTimeOffset.Now < LockoutUntil)
            return false;

        if (password == _lockPassword)
        {
            _lockPassword = null;
            _failedUnlockAttempts = 0;
            IsLocked = false;
            return true;
        }

        if (++_failedUnlockAttempts >= 3)
        {
            LockoutUntil = DateTimeOffset.Now.AddSeconds(30);
            _failedUnlockAttempts = 0;
        }
        return false;
    }
}
