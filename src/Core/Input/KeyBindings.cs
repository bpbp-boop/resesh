namespace Resesh.Core.Input;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>Where a shortcut is active.</summary>
public enum ShortcutScope
{
    /// <summary>Anywhere in the window. A focused terminal forwards it to the window.</summary>
    App,

    /// <summary>Only outside the terminal, because the chord belongs to the shell there.</summary>
    Window,

    /// <summary>Only while a terminal has focus. The terminal handles it itself.</summary>
    Terminal,

    /// <summary>Only while the session tree has focus.</summary>
    SessionTree,
}

/// <summary>Extra state a shortcut needs before it takes the key from the terminal.</summary>
public enum ShortcutCondition
{
    None,

    /// <summary>The window shows more than one tab group. Otherwise the key reaches the shell.</summary>
    Split,
}

/// <summary>One key combination. <see cref="VirtualKey"/> is the Windows virtual-key code,
/// which WinUI accelerators, the native terminal and the WebView2 page (KeyboardEvent.keyCode)
/// all report, so one number matches everywhere on every keyboard layout.</summary>
public sealed record KeyChord(KeyModifiers Modifiers, int VirtualKey, string KeyLabel)
{
    public IReadOnlyList<string> Parts
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(KeyModifiers.Ctrl))
                parts.Add("Ctrl");
            if (Modifiers.HasFlag(KeyModifiers.Alt))
                parts.Add("Alt");
            if (Modifiers.HasFlag(KeyModifiers.Shift))
                parts.Add("Shift");
            parts.Add(KeyLabel);
            return parts;
        }
    }

    public string Label => string.Join("+", Parts);

    public bool Matches(int virtualKey, KeyModifiers modifiers) =>
        VirtualKey == virtualKey && Modifiers == modifiers;

    public override string ToString() => Label;
}

public sealed record KeyBinding(
    string Id,
    string Category,
    string Title,
    ShortcutScope Scope,
    IReadOnlyList<KeyChord> Chords,
    string Note = "",
    ShortcutCondition Condition = ShortcutCondition.None,
    string? DisplayLabel = null)
{
    /// <summary>The text menus and the command palette show next to the action.</summary>
    public string Label => DisplayLabel ?? Chords[0].Label;
}

/// <summary>Stable shortcut identifiers. The terminal page matches the terminal-scope ids by name.</summary>
public static class ShortcutIds
{
    public const string CommandPalette = "app.commandPalette";
    public const string QuickConnect = "app.quickConnect";
    public const string NewLocalTab = "app.newLocalTab";
    public const string NewWindow = "app.newWindow";
    public const string Settings = "app.settings";
    public const string KeyboardShortcuts = "app.keyboardShortcuts";
    public const string FilterSessions = "view.filterSessions";
    public const string ToggleSessionsPane = "view.toggleSessionsPane";
    public const string FullScreen = "view.fullScreen";

    public const string NextTab = "tab.next";
    public const string PreviousTab = "tab.previous";
    public const string GoToTab = "tab.goTo";
    public const string LastTab = "tab.last";
    public const string CloseTab = "tab.close";
    public const string CloneTab = "tab.clone";
    public const string ReconnectTab = "tab.reconnect";
    public const string SendBreak = "tab.sendBreak";
    public const string MoveTabLeft = "tab.moveLeft";
    public const string MoveTabRight = "tab.moveRight";
    public const string FilePane = "tab.filePane";

    public const string SplitRight = "group.splitRight";
    public const string SplitDown = "group.splitDown";
    public const string FocusGroupLeft = "group.focusLeft";
    public const string FocusGroupRight = "group.focusRight";
    public const string FocusGroupUp = "group.focusUp";
    public const string FocusGroupDown = "group.focusDown";

    public const string Copy = "terminal.copy";
    public const string Paste = "terminal.paste";
    public const string SelectAll = "terminal.selectAll";
    public const string Find = "terminal.find";
    public const string ClearScrollback = "terminal.clearScrollback";
    public const string ScrollPageUp = "terminal.scrollPageUp";
    public const string ScrollPageDown = "terminal.scrollPageDown";
    public const string ScrollToTop = "terminal.scrollToTop";
    public const string ScrollToBottom = "terminal.scrollToBottom";
    public const string PreviousCommand = "terminal.previousCommand";
    public const string NextCommand = "terminal.nextCommand";
    public const string CommandsPanel = "terminal.commandsPanel";
    public const string Bookmark = "terminal.bookmark";
    public const string ZoomIn = "terminal.zoomIn";
    public const string ZoomOut = "terminal.zoomOut";
    public const string ZoomReset = "terminal.zoomReset";

    public const string OpenSelection = "tree.open";
    public const string EditSelection = "tree.edit";
    public const string DeleteSelection = "tree.delete";
}

/// <summary>
/// The one table of keyboard shortcuts. Window accelerators, both terminal surfaces, menus,
/// the command palette and the Keyboard Shortcuts screen all read it.
/// Plain Ctrl+letter chords are left to the shell (Ctrl+W, Ctrl+R, the tmux prefix Ctrl+B),
/// and no chord uses Ctrl+Alt, which is AltGr on many European layouts.
/// </summary>
public static class KeyBindings
{
    private const KeyModifiers Ctrl = KeyModifiers.Ctrl;
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Alt = KeyModifiers.Alt;
    private const KeyModifiers CtrlShift = KeyModifiers.Ctrl | KeyModifiers.Shift;

    public const string ApplicationCategory = "Application";
    public const string TabsCategory = "Tabs";
    public const string SplitCategory = "Split View";
    public const string TerminalCategory = "Terminal";
    public const string SessionTreeCategory = "Session Tree";

    /// <summary>Display order of the categories.</summary>
    public static IReadOnlyList<string> Categories { get; } =
        [ApplicationCategory, TabsCategory, SplitCategory, TerminalCategory, SessionTreeCategory];

    public static IReadOnlyList<KeyBinding> All { get; } = Build();

    private static readonly Dictionary<string, KeyBinding> ById =
        All.ToDictionary(binding => binding.Id, StringComparer.Ordinal);

    public static KeyBinding Get(string id) => ById[id];

    public static bool TryGet(string id, out KeyBinding binding) => ById.TryGetValue(id, out binding!);

    /// <summary>The first binding and chord in <paramref name="scopes"/> that the key matches.</summary>
    public static (KeyBinding Binding, int ChordIndex)? Find(
        int virtualKey, KeyModifiers modifiers, params ShortcutScope[] scopes)
    {
        foreach (var binding in All)
        {
            if (!scopes.Contains(binding.Scope))
                continue;
            for (var i = 0; i < binding.Chords.Count; i++)
            {
                if (binding.Chords[i].Matches(virtualKey, modifiers))
                    return (binding, i);
            }
        }
        return null;
    }

    private static KeyChord Chord(KeyModifiers modifiers, int virtualKey, string label) =>
        new(modifiers, virtualKey, label);

    private static KeyChord Letter(KeyModifiers modifiers, char letter) =>
        new(modifiers, letter, letter.ToString());

    private static List<KeyBinding> Build()
    {
        var list = new List<KeyBinding>();

        void Add(string id, string category, string title, ShortcutScope scope, KeyChord[] chords,
            string note = "", ShortcutCondition condition = ShortcutCondition.None, string? display = null) =>
            list.Add(new KeyBinding(id, category, title, scope, chords, note, condition, display));

        const string App = ApplicationCategory;
        Add(ShortcutIds.CommandPalette, App, "Command Palette", ShortcutScope.App, [Letter(CtrlShift, 'P')]);
        Add(ShortcutIds.QuickConnect, App, "Quick Connect", ShortcutScope.App, [Letter(CtrlShift, 'K')]);
        Add(ShortcutIds.NewLocalTab, App, "New Local Terminal", ShortcutScope.App, [Letter(CtrlShift, 'T')],
            "Opens the default local profile");
        Add(ShortcutIds.NewWindow, App, "New Window", ShortcutScope.App, [Letter(CtrlShift, 'N')]);
        Add(ShortcutIds.Settings, App, "Settings", ShortcutScope.App, [Chord(Ctrl, VirtualKeys.Comma, ",")]);
        Add(ShortcutIds.KeyboardShortcuts, App, "Keyboard Shortcuts", ShortcutScope.App,
            [Chord(CtrlShift, VirtualKeys.Slash, "/")]);
        Add(ShortcutIds.ToggleSessionsPane, App, "Show or Hide Sessions Pane", ShortcutScope.App,
            [Letter(CtrlShift, 'B')]);
        Add(ShortcutIds.FilterSessions, App, "Filter Sessions", ShortcutScope.Window, [Letter(Ctrl, 'F')],
            "Outside the terminal. In the terminal, Ctrl+F goes to the shell");
        Add(ShortcutIds.FullScreen, App, "Full Screen", ShortcutScope.App, [Chord(KeyModifiers.None, VirtualKeys.F11, "F11")]);

        const string Tabs = TabsCategory;
        Add(ShortcutIds.NextTab, Tabs, "Next Tab", ShortcutScope.App, [Chord(Ctrl, VirtualKeys.Tab, "Tab")]);
        Add(ShortcutIds.PreviousTab, Tabs, "Previous Tab", ShortcutScope.App, [Chord(CtrlShift, VirtualKeys.Tab, "Tab")]);
        Add(ShortcutIds.GoToTab, Tabs, "Go to Tab 1–8", ShortcutScope.App,
            [.. Enumerable.Range(1, 8).Select(n => Chord(Ctrl, '0' + n, n.ToString()))],
            display: "Ctrl+1 … Ctrl+8");
        Add(ShortcutIds.LastTab, Tabs, "Go to Last Tab", ShortcutScope.App, [Chord(Ctrl, '9', "9")]);
        Add(ShortcutIds.CloseTab, Tabs, "Close Tab", ShortcutScope.App,
            [Letter(CtrlShift, 'W'), Chord(Ctrl, VirtualKeys.F4, "F4")]);
        Add(ShortcutIds.CloneTab, Tabs, "Clone Tab", ShortcutScope.App, [Letter(CtrlShift, 'D')],
            "Opens the same session in a new tab");
        Add(ShortcutIds.ReconnectTab, Tabs, "Reconnect or Restart Tab", ShortcutScope.App, [Letter(CtrlShift, 'R')],
            "When the tab is disconnected or its shell has exited");
        Add(ShortcutIds.SendBreak, Tabs, "Send Break", ShortcutScope.App, [Chord(Ctrl, VirtualKeys.Cancel, "Break")],
            "Telnet tabs: console servers pass it to the device as a serial break (boot interrupt, password recovery)");
        Add(ShortcutIds.MoveTabLeft, Tabs, "Move Tab Left", ShortcutScope.App,
            [Chord(CtrlShift, VirtualKeys.PageUp, "PgUp")]);
        Add(ShortcutIds.MoveTabRight, Tabs, "Move Tab Right", ShortcutScope.App,
            [Chord(CtrlShift, VirtualKeys.PageDown, "PgDn")]);
        Add(ShortcutIds.FilePane, Tabs, "Show or Hide File Pane", ShortcutScope.App, [Letter(CtrlShift, 'E')]);

        const string Split = SplitCategory;
        Add(ShortcutIds.SplitRight, Split, "Split Right", ShortcutScope.App,
            [Chord(CtrlShift, VirtualKeys.Backslash, "\\")], "Moves the tab to a new group on the right");
        Add(ShortcutIds.SplitDown, Split, "Split Down", ShortcutScope.App,
            [Chord(CtrlShift, VirtualKeys.Minus, "-")], "Moves the tab to a new group below");
        const string SplitOnly = "When the window is split. Otherwise the key goes to the shell";
        Add(ShortcutIds.FocusGroupLeft, Split, "Focus Group on the Left", ShortcutScope.App,
            [Chord(Alt, VirtualKeys.Left, "←")], SplitOnly, ShortcutCondition.Split);
        Add(ShortcutIds.FocusGroupRight, Split, "Focus Group on the Right", ShortcutScope.App,
            [Chord(Alt, VirtualKeys.Right, "→")], SplitOnly, ShortcutCondition.Split);
        Add(ShortcutIds.FocusGroupUp, Split, "Focus Group Above", ShortcutScope.App,
            [Chord(Alt, VirtualKeys.Up, "↑")], SplitOnly, ShortcutCondition.Split);
        Add(ShortcutIds.FocusGroupDown, Split, "Focus Group Below", ShortcutScope.App,
            [Chord(Alt, VirtualKeys.Down, "↓")], SplitOnly, ShortcutCondition.Split);

        const string Term = TerminalCategory;
        Add(ShortcutIds.Copy, Term, "Copy", ShortcutScope.Terminal,
            [Letter(CtrlShift, 'C'), Chord(Ctrl, VirtualKeys.Insert, "Insert")],
            "When text is selected. Otherwise the key goes to the shell");
        Add(ShortcutIds.Paste, Term, "Paste", ShortcutScope.Terminal,
            [Letter(CtrlShift, 'V'), Chord(Shift, VirtualKeys.Insert, "Insert")]);
        Add(ShortcutIds.SelectAll, Term, "Select All", ShortcutScope.Terminal, [Letter(CtrlShift, 'A')]);
        Add(ShortcutIds.Find, Term, "Find", ShortcutScope.Terminal, [Letter(CtrlShift, 'F')]);
        Add(ShortcutIds.ClearScrollback, Term, "Clear Scrollback", ShortcutScope.Terminal, [Letter(CtrlShift, 'L')],
            "Keeps the current line. Ctrl+L still clears only the screen");
        Add(ShortcutIds.ScrollPageUp, Term, "Scroll Up One Page", ShortcutScope.Terminal,
            [Chord(Shift, VirtualKeys.PageUp, "PgUp")]);
        Add(ShortcutIds.ScrollPageDown, Term, "Scroll Down One Page", ShortcutScope.Terminal,
            [Chord(Shift, VirtualKeys.PageDown, "PgDn")]);
        Add(ShortcutIds.ScrollToTop, Term, "Scroll to Top", ShortcutScope.Terminal,
            [Chord(CtrlShift, VirtualKeys.Home, "Home")]);
        Add(ShortcutIds.ScrollToBottom, Term, "Scroll to Bottom", ShortcutScope.Terminal,
            [Chord(CtrlShift, VirtualKeys.End, "End")]);
        Add(ShortcutIds.PreviousCommand, Term, "Previous Command", ShortcutScope.Terminal,
            [Chord(CtrlShift, VirtualKeys.Up, "↑")]);
        Add(ShortcutIds.NextCommand, Term, "Next Command", ShortcutScope.Terminal,
            [Chord(CtrlShift, VirtualKeys.Down, "↓")]);
        Add(ShortcutIds.CommandsPanel, Term, "Show or Hide Commands Panel", ShortcutScope.Terminal,
            [Letter(CtrlShift, 'O')]);
        Add(ShortcutIds.Bookmark, Term, "Toggle Bookmark", ShortcutScope.Terminal, [Letter(CtrlShift, 'M')]);
        Add(ShortcutIds.ZoomIn, Term, "Zoom In", ShortcutScope.Terminal,
            [Chord(Ctrl, VirtualKeys.OemPlus, "="), Chord(Ctrl, VirtualKeys.NumpadAdd, "Num +")],
            "Also Ctrl+mouse wheel. Zoom applies to this tab only");
        Add(ShortcutIds.ZoomOut, Term, "Zoom Out", ShortcutScope.Terminal,
            [Chord(Ctrl, VirtualKeys.Minus, "-"), Chord(Ctrl, VirtualKeys.NumpadSubtract, "Num -")]);
        Add(ShortcutIds.ZoomReset, Term, "Reset Zoom", ShortcutScope.Terminal,
            [Chord(Ctrl, '0', "0"), Chord(Ctrl, VirtualKeys.Numpad0, "Num 0")]);

        const string Tree = SessionTreeCategory;
        Add(ShortcutIds.OpenSelection, Tree, "Connect or Open", ShortcutScope.SessionTree,
            [Chord(KeyModifiers.None, VirtualKeys.Enter, "Enter")], "Opens every selected session");
        Add(ShortcutIds.EditSelection, Tree, "Rename Folder or Edit Session", ShortcutScope.SessionTree,
            [Chord(KeyModifiers.None, VirtualKeys.F2, "F2")]);
        Add(ShortcutIds.DeleteSelection, Tree, "Delete", ShortcutScope.SessionTree,
            [Chord(KeyModifiers.None, VirtualKeys.Delete, "Delete")], "Asks before deleting");

        return list;
    }
}

/// <summary>Windows virtual-key codes used by the table (winuser.h).</summary>
public static class VirtualKeys
{
    public const int Tab = 0x09;
    public const int Cancel = 0x03; // Ctrl+Pause/Break reports VK_CANCEL
    public const int Enter = 0x0D;
    public const int PageUp = 0x21;
    public const int PageDown = 0x22;
    public const int End = 0x23;
    public const int Home = 0x24;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;
    public const int Insert = 0x2D;
    public const int Delete = 0x2E;
    public const int Numpad0 = 0x60;
    public const int NumpadAdd = 0x6B;
    public const int NumpadSubtract = 0x6D;
    public const int F2 = 0x71;
    public const int F4 = 0x73;
    public const int F11 = 0x7A;
    public const int OemPlus = 0xBB; // VK_OEM_PLUS: the "=/+" key
    public const int Comma = 0xBC;
    public const int Minus = 0xBD;
    public const int Slash = 0xBF; // VK_OEM_2
    public const int Backslash = 0xDC; // VK_OEM_5
}
