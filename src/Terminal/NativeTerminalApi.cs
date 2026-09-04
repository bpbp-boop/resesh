using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Resesh.Terminal;

/// <summary>Versioned adapter for the resesh ABI exported by Microsoft.Terminal.Control.dll.</summary>
internal sealed class NativeTerminalApi
{
    internal const ushort AbiMajor = 2;
    internal const ushort AbiMinor = 2;
    internal const string DllEnvironmentVariable = "RESESH_NATIVE_TERMINAL_DLL";

    private const uint ThemeOption = 0x00000001;
    private const uint InteractionOption = 0x00000002;
    private const uint EnableBuiltinGlyphs = 0x00000001;
    private const uint EnableColorGlyphs = 0x00000002;
    private const uint DetectUrls = 0x00000004;
    private const uint CopyOnSelect = 0x00000008;
    private const uint RightClickPaste = 0x00000010;
    private const uint SnapOnInput = 0x00000020;
    private const uint AllowOscClipboard = 0x00000040;
    private const uint AllowOscNotifications = 0x00000080;
    private const uint ReadOnly = 0x00000100;
    private const uint CopyHtml = 0x00000001;
    private const uint CopyRtf = 0x00000002;
    private const uint FilterCrLf = 0x00000001;
    private const uint FilterControlCodes = 0x00000002;
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const int InsufficientBuffer = unchecked((int)0x8007007A);

    private static readonly Lazy<NativeTerminalApi> Shared = new(Create);
    internal static NativeTerminalApi Instance => Shared.Value;

    private readonly IntPtr _module;
    private readonly CreateDelegate _create;
    private readonly DestroyDelegate _destroy;
    private readonly RegisterEventCallbackDelegate _registerEventCallback;
    private readonly SendOutputDelegate _sendOutput;
    private readonly SendKeyEventDelegate _sendKeyEvent;
    private readonly SendCharEventDelegate _sendCharEvent;
    private readonly SetFocusedDelegate _setFocused;
    private readonly SetBoundsDelegate _setBounds;
    private readonly AttachSwapChainPanelDelegate _attachSwapChainPanel;
    private readonly SendPointerEventDelegate _sendPointerEvent;
    private readonly SetOptionsDelegate _setOptions;
    private readonly CopySelectionDelegate _copySelection;
    private readonly PasteTextDelegate _pasteText;
    private readonly SearchDelegate _search;
    private readonly ClearSearchDelegate _clearSearch;
    private readonly GetSearchStateDelegate _getSearchState;
    private readonly UserScrollDelegate _userScroll;
    private readonly GetMarksDelegate _getMarks;
    private readonly GetSearchRowsDelegate _getSearchRows;
    private readonly GetMarkTextDelegate _getMarkText;
    private readonly ScrollToMarkDelegate _scrollToMark;
    private readonly GetCursorLogicalLineDelegate _getCursorLogicalLine;
    private readonly CreateApplicationMarkDelegate _createApplicationMark;
    private readonly DiscardPromptProbeDelegate _discardPromptProbe;
    private readonly AddBookmarkDelegate _addBookmark;
    private readonly RemoveBookmarkDelegate _removeBookmark;
    private readonly ClearBookmarksDelegate _clearBookmarks;
    private readonly ResizeCharactersDelegate _resizeCharacters;
    private readonly CaptureSnapshotDelegate _captureSnapshot;
    private readonly SetHighlightRulesDelegate _setHighlightRules;
    private readonly ClearHighlightRulesDelegate _clearHighlightRules;
    private readonly GetHighlightRowsDelegate _getHighlightRows;

    private NativeTerminalApi(IntPtr module, string libraryPath)
    {
        _module = module;
        LibraryPath = libraryPath;

        var getAbiVersion = Load<GetAbiVersionDelegate>("ReseshTerminalGetAbiVersion");
        var packedVersion = getAbiVersion();
        var major = checked((ushort)(packedVersion >> 16));
        var minor = checked((ushort)(packedVersion & 0xffff));
        if (major != AbiMajor || minor < AbiMinor)
        {
            throw new InvalidOperationException(
                $"The native terminal ABI is {major}.{minor}; resesh requires {AbiMajor}.{AbiMinor}.");
        }
        AbiVersion = new Version(major, minor);

        BuildId = ReadBuildId(Load<GetBuildIdDelegate>("ReseshTerminalGetBuildId"));
        _create = Load<CreateDelegate>("ReseshTerminalCreate");
        _destroy = Load<DestroyDelegate>("ReseshTerminalDestroy");
        _registerEventCallback = Load<RegisterEventCallbackDelegate>("ReseshTerminalRegisterEventCallback");
        _sendOutput = Load<SendOutputDelegate>("ReseshTerminalSendOutput");
        _sendKeyEvent = Load<SendKeyEventDelegate>("ReseshTerminalSendKeyEvent");
        _sendCharEvent = Load<SendCharEventDelegate>("ReseshTerminalSendCharEvent");
        _setFocused = Load<SetFocusedDelegate>("ReseshTerminalSetFocused");
        _setBounds = Load<SetBoundsDelegate>("ReseshTerminalSetBounds");
        _attachSwapChainPanel = Load<AttachSwapChainPanelDelegate>("ReseshTerminalAttachSwapChainPanel");
        _sendPointerEvent = Load<SendPointerEventDelegate>("ReseshTerminalSendPointerEvent");
        _setOptions = Load<SetOptionsDelegate>("ReseshTerminalSetOptions");
        _copySelection = Load<CopySelectionDelegate>("ReseshTerminalCopySelection");
        _pasteText = Load<PasteTextDelegate>("ReseshTerminalPasteText");
        _search = Load<SearchDelegate>("ReseshTerminalSearch");
        _clearSearch = Load<ClearSearchDelegate>("ReseshTerminalClearSearch");
        _getSearchState = Load<GetSearchStateDelegate>("ReseshTerminalGetSearchState");
        _userScroll = Load<UserScrollDelegate>("ReseshTerminalUserScroll");
        _getMarks = Load<GetMarksDelegate>("ReseshTerminalGetMarks");
        _getSearchRows = Load<GetSearchRowsDelegate>("ReseshTerminalGetSearchRows");
        _getMarkText = Load<GetMarkTextDelegate>("ReseshTerminalGetMarkText");
        _scrollToMark = Load<ScrollToMarkDelegate>("ReseshTerminalScrollToMark");
        _getCursorLogicalLine = Load<GetCursorLogicalLineDelegate>("ReseshTerminalGetCursorLogicalLine");
        _createApplicationMark = Load<CreateApplicationMarkDelegate>("ReseshTerminalCreateApplicationMark");
        _discardPromptProbe = Load<DiscardPromptProbeDelegate>("ReseshTerminalDiscardPromptProbe");
        _addBookmark = Load<AddBookmarkDelegate>("ReseshTerminalAddBookmark");
        _removeBookmark = Load<RemoveBookmarkDelegate>("ReseshTerminalRemoveBookmark");
        _clearBookmarks = Load<ClearBookmarksDelegate>("ReseshTerminalClearBookmarks");
        _resizeCharacters = Load<ResizeCharactersDelegate>("ReseshTerminalResizeCharacters");
        _captureSnapshot = Load<CaptureSnapshotDelegate>("ReseshTerminalCaptureSnapshot");
        _setHighlightRules = Load<SetHighlightRulesDelegate>("ReseshTerminalSetHighlightRules");
        _clearHighlightRules = Load<ClearHighlightRulesDelegate>("ReseshTerminalClearHighlightRules");
        _getHighlightRows = Load<GetHighlightRowsDelegate>("ReseshTerminalGetHighlightRows");
    }

    internal string LibraryPath { get; }
    internal Version AbiVersion { get; }
    internal string BuildId { get; }

    internal IntPtr CreateTerminal(
        IntPtr hostHwnd,
        NativeTerminalCreationSettings settings)
    {
        var theme = settings.Theme;
        var flags = EnableBuiltinGlyphs | EnableColorGlyphs | DetectUrls | SnapOnInput;
        if (settings.CopyOnSelect)
            flags |= CopyOnSelect;
        if (settings.RightClickPaste)
            flags |= RightClickPaste;
        if (settings.AllowOscClipboard)
            flags |= AllowOscClipboard;
        if (settings.AllowOscNotifications)
            flags |= AllowOscNotifications;
        if (settings.ReadOnly)
            flags |= ReadOnly;

        const string wordDelimiters = " ./\\()\"'-:,.;<>~!@#$%^&*|+=[]{}~?\u2502";
        var options = new CreateOptions
        {
            StructSize = checked((uint)Marshal.SizeOf<CreateOptions>()),
            AbiMajor = AbiMajor,
            AbiMinor = AbiMinor,
            HostHwnd = hostHwnd,
            InitialColumns = settings.InitialColumns,
            InitialRows = settings.InitialRows,
            HistorySize = settings.HistorySize,
            Flags = flags,
            FontFamily = settings.FontFamily,
            FontFamilyLength = checked((uint)settings.FontFamily.Length),
            FontSize = checked((short)settings.FontSize),
            FontWeight = 400,
            DefaultBackground = theme.DefaultBackground,
            DefaultForeground = theme.DefaultForeground,
            SelectionBackground = theme.DefaultSelectionBackground,
            CursorColor = theme.CursorColor,
            CursorStyle = theme.CursorStyle,
            ColorTable = theme.ColorTable,
            CopyFormatting = CopyHtml | CopyRtf,
            PasteFiltering = FilterCrLf | FilterControlCodes,
            WordDelimiters = wordDelimiters,
            WordDelimitersLength = checked((uint)wordDelimiters.Length),
        };
        ThrowIfFailed(_create(in options, out var terminal));
        return terminal;
    }

    internal void DestroyTerminal(IntPtr terminal)
    {
        if (terminal != IntPtr.Zero)
            ThrowIfFailed(_destroy(terminal));
    }

    internal void RegisterEventCallback(IntPtr terminal, EventCallback callback) =>
        ThrowIfFailed(_registerEventCallback(terminal, callback, IntPtr.Zero));

    internal void SendOutput(IntPtr terminal, string text) =>
        ThrowIfFailed(_sendOutput(terminal, text, checked((uint)text.Length)));

    internal bool SendKeyEvent(IntPtr terminal, ushort virtualKey, ushort scanCode, ushort flags, bool keyDown)
    {
        var result = _sendKeyEvent(
            terminal,
            virtualKey,
            scanCode,
            flags,
            keyDown ? (byte)1 : (byte)0,
            out var handled);
        ThrowIfFailed(result);
        return handled != 0;
    }

    internal void SendCharEvent(IntPtr terminal, char character, ushort scanCode, ushort flags) =>
        ThrowIfFailed(_sendCharEvent(terminal, character, scanCode, flags));

    internal void SetFocused(IntPtr terminal, bool focused) =>
        ThrowIfFailed(_setFocused(terminal, focused ? (byte)1 : (byte)0));

    internal void ResizeCharacters(IntPtr terminal, int columns, int rows) =>
        ThrowIfFailed(_resizeCharacters(terminal, columns, rows));

    internal Snapshot CaptureSnapshot(IntPtr terminal)
    {
        var snapshot = new NativeSnapshot
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeSnapshot>()),
            AbiMajor = AbiMajor,
            AbiMinor = AbiMinor,
        };
        var result = _captureSnapshot(terminal, ref snapshot, IntPtr.Zero, 0, out var required);
        if (result != InsufficientBuffer)
            ThrowIfFailed(result);
        if (required is 0 or > 32 * 1024 * 1024)
            throw new InvalidDataException("The native terminal returned an invalid snapshot length.");

        var payload = Marshal.AllocHGlobal(checked((int)required * sizeof(char)));
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                result = _captureSnapshot(terminal, ref snapshot, payload, required, out var written);
                if (result == InsufficientBuffer && written > required && written <= 32 * 1024 * 1024)
                {
                    var oldPayload = payload;
                    payload = IntPtr.Zero;
                    Marshal.FreeHGlobal(oldPayload);
                    required = written;
                    payload = Marshal.AllocHGlobal(checked((int)required * sizeof(char)));
                    continue;
                }
                ThrowIfFailed(result);
                ValidateSnapshot(in snapshot, written);
                return new Snapshot(
                    snapshot.SchemaVersion,
                    snapshot.Columns,
                    snapshot.Rows,
                    snapshot.CursorColumn,
                    snapshot.CursorRow,
                    snapshot.ViewportTop,
                    snapshot.ViewportHeight,
                    snapshot.ScrollOffset,
                    (snapshot.Flags & 1) != 0,
                    (snapshot.Flags & 2) != 0,
                    snapshot.CaptureSequence,
                    snapshot.UnixTimeMilliseconds,
                    ReadSnapshotText(payload, snapshot.AnsiOffset, snapshot.AnsiLength),
                    ReadSnapshotText(payload, snapshot.TitleOffset, snapshot.TitleLength),
                    ReadSnapshotText(payload, snapshot.WorkingDirectoryOffset, snapshot.WorkingDirectoryLength));
            }
            throw new InvalidDataException("The native terminal snapshot changed size too often.");
        }
        finally
        {
            if (payload != IntPtr.Zero)
                Marshal.FreeHGlobal(payload);
        }
    }

    internal bool CopySelection(IntPtr terminal, bool clearSelection)
    {
        var result = _copySelection(terminal, clearSelection ? (byte)1 : (byte)0);
        ThrowIfFailed(result);
        return result == 0;
    }

    internal void PasteText(IntPtr terminal, string text) =>
        ThrowIfFailed(_pasteText(terminal, text, checked((uint)text.Length)));

    internal SearchState Search(
        IntPtr terminal,
        string query,
        bool forward,
        bool caseSensitive,
        bool regularExpression,
        bool executeSearch,
        bool scrollIntoView)
    {
        uint flags = 0;
        if (forward)
            flags |= 0x01;
        if (caseSensitive)
            flags |= 0x02;
        if (regularExpression)
            flags |= 0x04;
        if (executeSearch)
            flags |= 0x08;
        if (scrollIntoView)
            flags |= 0x10;
        var request = new SearchRequest
        {
            StructSize = checked((uint)Marshal.SizeOf<SearchRequest>()),
            AbiMajor = AbiMajor,
            AbiMinor = AbiMinor,
            Query = query,
            QueryLength = checked((uint)query.Length),
            Flags = flags,
        };
        ThrowIfFailed(_search(terminal, in request, out var state));
        return ToSearchState(state);
    }

    internal void ClearSearch(IntPtr terminal) => ThrowIfFailed(_clearSearch(terminal));

    internal SearchState GetSearchState(IntPtr terminal)
    {
        ThrowIfFailed(_getSearchState(terminal, out var state));
        return ToSearchState(state);
    }

    internal void UserScroll(IntPtr terminal, int viewTop) =>
        ThrowIfFailed(_userScroll(terminal, viewTop));

    internal IReadOnlyList<MarkRecord> GetMarks(IntPtr terminal)
    {
        var result = _getMarks(terminal, IntPtr.Zero, 0, out var required);
        if (required == 0)
        {
            ThrowIfFailed(result);
            return [];
        }
        if (result != InsufficientBuffer)
            ThrowIfFailed(result);
        var size = Marshal.SizeOf<NativeMarkRecord>();
        var buffer = Marshal.AllocHGlobal(checked(size * (int)required));
        try
        {
            ThrowIfFailed(_getMarks(terminal, buffer, required, out var written));
            if (written != required)
                throw new InvalidDataException("The native terminal changed its mark count.");
            var records = new MarkRecord[written];
            for (var index = 0; index < records.Length; index++)
            {
                var native = Marshal.PtrToStructure<NativeMarkRecord>(buffer + index * size);
                records[index] = new MarkRecord(
                    native.Id,
                    native.Generation,
                    (MarkKind)native.Kind,
                    native.Flags,
                    native.Category,
                    native.Color,
                    (native.Flags & 0x01) != 0 ? native.ExitCode : null,
                    native.StartY);
            }
            return records;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal IReadOnlyList<int> GetSearchRows(IntPtr terminal)
    {
        var result = _getSearchRows(terminal, IntPtr.Zero, 0, out var required);
        if (required == 0)
        {
            ThrowIfFailed(result);
            return [];
        }
        if (result != InsufficientBuffer)
            ThrowIfFailed(result);
        var buffer = Marshal.AllocHGlobal(checked((int)required * sizeof(int)));
        try
        {
            ThrowIfFailed(_getSearchRows(terminal, buffer, required, out var written));
            if (written != required)
                throw new InvalidDataException("The native terminal changed its search row count.");
            var rows = new int[written];
            Marshal.Copy(buffer, rows, 0, rows.Length);
            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal void SetHighlightRules(IntPtr terminal, IReadOnlyList<HighlightRulePayload> rules)
    {
        var native = new NativeHighlightRule[rules.Count];
        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            uint flags = 0;
            if (r.RegularExpression)
                flags |= 0x01;
            if (r.MatchCase)
                flags |= 0x02;
            if (r.ShowInOverview)
                flags |= 0x04;
            native[i] = new NativeHighlightRule
            {
                StructSize = checked((uint)Marshal.SizeOf<NativeHighlightRule>()),
                AbiMajor = AbiMajor,
                AbiMinor = AbiMinor,
                Id = r.Id,
                Pattern = r.Pattern,
                PatternLength = checked((uint)r.Pattern.Length),
                Flags = flags,
                Foreground = r.Foreground,
                Background = r.Background,
                Priority = r.Priority,
            };
        }
        ThrowIfFailed(_setHighlightRules(terminal, native, checked((uint)native.Length)));
    }

    internal void ClearHighlightRules(IntPtr terminal) =>
        ThrowIfFailed(_clearHighlightRules(terminal));

    internal IReadOnlyList<HighlightRowRecord> GetHighlightRows(IntPtr terminal)
    {
        var result = _getHighlightRows(terminal, IntPtr.Zero, 0, out var required);
        if (required == 0)
        {
            ThrowIfFailed(result);
            return [];
        }
        if (result != InsufficientBuffer)
            ThrowIfFailed(result);
        var size = Marshal.SizeOf<NativeHighlightRow>();
        var buffer = Marshal.AllocHGlobal(checked(size * (int)required));
        try
        {
            ThrowIfFailed(_getHighlightRows(terminal, buffer, required, out var written));
            if (written != required)
                throw new InvalidDataException("The native terminal changed its highlight row count.");
            var records = new HighlightRowRecord[written];
            for (var index = 0; index < records.Length; index++)
            {
                var item = Marshal.PtrToStructure<NativeHighlightRow>(buffer + index * size);
                records[index] = new HighlightRowRecord(item.Row, item.Count, item.Color);
            }
            return records;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal string GetMarkText(IntPtr terminal, ulong markId, bool includeOutput) =>
        ReadNativeText((IntPtr buffer, uint capacity, out uint required) =>
            _getMarkText(terminal, markId, includeOutput ? (byte)1 : (byte)0, buffer, capacity, out required));

    internal void ScrollToMark(IntPtr terminal, ulong markId) =>
        ThrowIfFailed(_scrollToMark(terminal, markId));

    internal PromptProbe BeginPromptProbe(IntPtr terminal)
    {
        var line = new NativeCursorLogicalLine();
        var result = _getCursorLogicalLine(terminal, out line, IntPtr.Zero, 0, out var required);
        if (result != InsufficientBuffer)
            ThrowIfFailed(result);
        if (required is 0 or > 1024 * 1024)
            throw new InvalidDataException("The native terminal returned an invalid cursor line length.");
        var buffer = Marshal.AllocHGlobal(checked((int)required * sizeof(char)));
        try
        {
            ThrowIfFailed(_getCursorLogicalLine(terminal, out line, buffer, required, out var written));
            if (written != required)
                throw new InvalidDataException("The native terminal changed its cursor line length.");
            return new PromptProbe(
                line.ProbeId,
                line.Generation,
                line.StartRow,
                line.CursorRow,
                line.CursorColumn,
                Marshal.PtrToStringUni(buffer, checked((int)written - 1)) ?? string.Empty);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal void CreateApplicationMark(IntPtr terminal, ulong probeId, string command, int? exitCode = null) =>
        ThrowIfFailed(_createApplicationMark(
            terminal,
            probeId,
            command,
            checked((uint)command.Length),
            exitCode ?? 0,
            exitCode.HasValue ? (byte)1 : (byte)0));

    internal void DiscardPromptProbe(IntPtr terminal, ulong probeId) =>
        ThrowIfFailed(_discardPromptProbe(terminal, probeId));

    internal ulong AddBookmark(IntPtr terminal, int row, uint? color)
    {
        ThrowIfFailed(_addBookmark(terminal, row, color ?? 0, color.HasValue ? (byte)1 : (byte)0, out var bookmarkId));
        return bookmarkId;
    }

    internal void RemoveBookmark(IntPtr terminal, ulong bookmarkId) =>
        ThrowIfFailed(_removeBookmark(terminal, bookmarkId));

    internal void ClearBookmarks(IntPtr terminal) =>
        ThrowIfFailed(_clearBookmarks(terminal));

    private delegate int NativeTextReader(IntPtr buffer, uint capacity, out uint requiredCapacity);

    private static string ReadNativeText(NativeTextReader reader)
    {
        var result = reader(IntPtr.Zero, 0, out var required);
        if (result != InsufficientBuffer)
            ThrowIfFailed(result);
        if (required is 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("The native terminal returned an invalid text length.");
        var buffer = Marshal.AllocHGlobal(checked((int)required * sizeof(char)));
        try
        {
            ThrowIfFailed(reader(buffer, required, out var written));
            if (written != required)
                throw new InvalidDataException("The native terminal changed its text length.");
            return Marshal.PtrToStringUni(buffer, checked((int)written - 1)) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static SearchState ToSearchState(NativeSearchState state) =>
        new(
            state.TotalMatches,
            state.CurrentMatch,
            (state.Flags & 0x01) != 0,
            (state.Flags & 0x02) != 0);

    internal TilSize SetBounds(IntPtr terminal, int screenX, int screenY, int width, int height)
    {
        ThrowIfFailed(_setBounds(terminal, screenX, screenY, width, height, out var columns, out var rows));
        return new TilSize { X = columns, Y = rows };
    }

    internal void AttachSwapChainPanel(IntPtr terminal, IntPtr panel) =>
        ThrowIfFailed(_attachSwapChainPanel(terminal, panel));

    internal uint SendPointerEvent(
        IntPtr terminal,
        uint message,
        uint buttons,
        int x,
        int y,
        short wheelDelta)
    {
        ThrowIfFailed(_sendPointerEvent(terminal, message, buttons, x, y, wheelDelta, out var result));
        return result;
    }
    internal void SetTheme(IntPtr terminal, TerminalTheme theme, string fontFamily, short fontSize, int dpi)
    {
        var options = new TerminalOptions
        {
            StructSize = checked((uint)Marshal.SizeOf<TerminalOptions>()),
            AbiMajor = AbiMajor,
            AbiMinor = AbiMinor,
            Flags = ThemeOption,
            DefaultBackground = theme.DefaultBackground,
            DefaultForeground = theme.DefaultForeground,
            DefaultSelectionBackground = theme.DefaultSelectionBackground,
            CursorColor = theme.CursorColor,
            CursorStyle = theme.CursorStyle,
            ColorTable = theme.ColorTable,
            FontFamily = fontFamily,
            FontFamilyLength = checked((uint)fontFamily.Length),
            FontSize = fontSize,
            Dpi = dpi,
        };
        ThrowIfFailed(_setOptions(terminal, in options));
    }

    internal void SetInteraction(
        IntPtr terminal,
        bool copyOnSelect,
        bool rightClickPaste,
        bool readOnly)
    {
        var interactionFlags = readOnly ? ReadOnly : 0;
        if (copyOnSelect)
            interactionFlags |= CopyOnSelect;
        if (rightClickPaste)
            interactionFlags |= RightClickPaste;
        var options = new TerminalOptions
        {
            StructSize = checked((uint)Marshal.SizeOf<TerminalOptions>()),
            AbiMajor = AbiMajor,
            AbiMinor = AbiMinor,
            Flags = InteractionOption,
            ColorTable = new uint[16],
            FontFamily = string.Empty,
            InteractionFlags = interactionFlags,
            CopyFormatting = CopyHtml | CopyRtf,
            PasteFiltering = FilterCrLf | FilterControlCodes,
        };
        ThrowIfFailed(_setOptions(terminal, in options));
    }

    private static NativeTerminalApi Create()
    {
        var path = ResolveLibraryPath();
        VerifyArchitecture(path);
        VerifyAppLocalHash(path);

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The native terminal DLL path has no parent directory.");
        var directoryCookie = AddDllDirectory(directory);
        if (directoryCookie == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Could not add the native terminal DLL directory '{directory}' (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        IntPtr module;
        int loadError = 0;
        try
        {
            module = LoadLibraryEx(
                path,
                IntPtr.Zero,
                LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
            if (module == IntPtr.Zero)
                loadError = Marshal.GetLastPInvokeError();
        }
        finally
        {
            _ = RemoveDllDirectory(directoryCookie);
        }

        if (module == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Could not load the native terminal DLL '{path}' (Win32 error {loadError}).");
        }
        try
        {
            return new NativeTerminalApi(module, path);
        }
        catch
        {
            _ = FreeLibrary(module);
            throw;
        }
    }

    private static string ResolveLibraryPath()
    {
        var configured = Environment.GetEnvironmentVariable(DllEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var fullPath = Path.GetFullPath(configured);
            if (File.Exists(fullPath))
                return fullPath;
            throw new FileNotFoundException(
                $"{DllEnvironmentVariable} points to a file that does not exist.", fullPath);
        }

        var appLocalPath = Path.Combine(
            AppContext.BaseDirectory,
            "NativeTerminal",
            ArchitectureName(),
            "Microsoft.Terminal.Control.dll");
        if (File.Exists(appLocalPath))
            return appLocalPath;

        throw new FileNotFoundException(
            "The native terminal surface is selected, but its pinned DLL is missing. " +
            $"Set {DllEnvironmentVariable}, or build the artifact at '{appLocalPath}'.",
            appLocalPath);
    }

    private static void VerifyArchitecture(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0x5a4d)
            throw new BadImageFormatException("The native terminal DLL has no DOS header.", path);
        stream.Position = 0x3c;
        var peOffset = reader.ReadUInt32();
        if (peOffset > stream.Length - 6)
            throw new BadImageFormatException("The native terminal DLL has an invalid PE header offset.", path);
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550)
            throw new BadImageFormatException("The native terminal DLL has no PE signature.", path);

        var machine = reader.ReadUInt16();
        var expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (ushort)0x8664,
            Architecture.Arm64 => (ushort)0xaa64,
            var architecture => throw new PlatformNotSupportedException(
                $"The native terminal does not support {architecture} processes."),
        };
        if (machine != expected)
        {
            throw new BadImageFormatException(
                $"The native terminal DLL machine 0x{machine:x4} does not match " +
                $"the {RuntimeInformation.ProcessArchitecture} process (0x{expected:x4}).",
                path);
        }
    }

    private static void VerifyAppLocalHash(string libraryPath)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DllEnvironmentVariable)))
            return;

        var manifestPath = Path.Combine(AppContext.BaseDirectory, "NativeTerminal", "native-terminal.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The native terminal artifact manifest is missing.", manifestPath);

        // Read as text so StreamReader removes the optional UTF-8 BOM used by older manifests.
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var architecture = ArchitectureName();
        var expected = document.RootElement
            .GetProperty("artifacts")
            .GetProperty(architecture)
            .GetProperty("Microsoft.Terminal.Control.dll")
            .GetString();
        if (string.IsNullOrWhiteSpace(expected))
            throw new InvalidDataException($"The native terminal manifest has no DLL hash for {architecture}.");

        using var library = File.OpenRead(libraryPath);
        var actual = Convert.ToHexString(SHA256.HashData(library)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The native terminal DLL hash is {actual}, but the manifest requires {expected}.");
        }
    }

    private static string ArchitectureName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        var architecture => throw new PlatformNotSupportedException(
            $"The native terminal does not support {architecture} processes."),
    };

    private static string ReadBuildId(GetBuildIdDelegate getBuildId)
    {
        _ = getBuildId(IntPtr.Zero, 0, out var required);
        if (required is 0 or > 4096)
            throw new InvalidDataException("The native terminal returned an invalid build ID length.");

        var buffer = Marshal.AllocHGlobal(checked((int)required * sizeof(char)));
        try
        {
            ThrowIfFailed(getBuildId(buffer, required, out var written));
            if (written != required)
                throw new InvalidDataException("The native terminal changed its build ID length.");
            return Marshal.PtrToStringUni(buffer, checked((int)required - 1))
                ?? throw new InvalidDataException("The native terminal returned an empty build ID.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ValidateSnapshot(in NativeSnapshot snapshot, uint payloadLength)
    {
        if (snapshot.StructSize < Marshal.SizeOf<NativeSnapshot>()
            || snapshot.AbiMajor != AbiMajor
            || snapshot.AbiMinor < AbiMinor
            || snapshot.SchemaVersion != 1
            || snapshot.Columns <= 0
            || snapshot.Rows <= 0
            || snapshot.ViewportHeight <= 0
            || snapshot.ViewportHeight != snapshot.Rows
            || snapshot.CursorColumn < 0
            || snapshot.CursorColumn >= snapshot.Columns
            || snapshot.CursorRow < 0
            || snapshot.CursorRow >= snapshot.ViewportHeight
            || snapshot.ViewportTop < 0
            || snapshot.ScrollOffset < 0
            || (snapshot.Flags & ~3u) != 0
            || snapshot.UnixTimeMilliseconds < 0
            || !Fits(snapshot.AnsiOffset, snapshot.AnsiLength, payloadLength)
            || !Fits(snapshot.TitleOffset, snapshot.TitleLength, payloadLength)
            || !Fits(snapshot.WorkingDirectoryOffset, snapshot.WorkingDirectoryLength, payloadLength))
        {
            throw new InvalidDataException("The native terminal returned an invalid snapshot.");
        }
    }

    private static bool Fits(uint offset, uint length, uint payloadLength) =>
        offset <= payloadLength && length <= payloadLength - offset;

    private static string ReadSnapshotText(IntPtr payload, uint offset, uint length) =>
        length == 0
            ? string.Empty
            : Marshal.PtrToStringUni(
                IntPtr.Add(payload, checked((int)offset * sizeof(char))),
                checked((int)length)) ?? string.Empty;

    private T Load<T>(string exportName) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_module, exportName, out var address))
        {
            throw new EntryPointNotFoundException(
                $"The native terminal ABI {AbiMajor}.{AbiMinor} requires export '{exportName}'.");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDllDirectory(IntPtr cookie);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CreateOptions
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;
        internal IntPtr HostHwnd;
        internal int InitialColumns;
        internal int InitialRows;
        internal int HistorySize;
        internal uint Flags;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string FontFamily;
        internal uint FontFamilyLength;
        internal short FontSize;
        internal ushort FontWeight;
        internal uint DefaultBackground;
        internal uint DefaultForeground;
        internal uint SelectionBackground;
        internal uint CursorColor;
        internal uint CursorStyle;

        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U4, SizeConst = 16)]
        internal uint[] ColorTable;

        internal uint CopyFormatting;
        internal uint PasteFiltering;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string WordDelimiters;
        internal uint WordDelimitersLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeEvent
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;
        internal uint Type;
        internal uint Flags;
        internal ulong Sequence;
        internal IntPtr Text;
        internal uint TextLength;
        internal IntPtr Html;
        internal uint HtmlLength;
        internal IntPtr Rtf;
        internal uint RtfLength;
        internal long Value0;
        internal long Value1;
        internal long Value2;
    }

    internal enum NativeEventType : uint
    {
        Input = 1,
        ClipboardCopy = 2,
        ClipboardPasteRequest = 3,
        TitleChanged = 4,
        WorkingDirectoryChanged = 5,
        Bell = 6,
        BufferOrViewportChanged = 7,
        AlternateBufferChanged = 8,
        ShellIntegrationMarkChanged = 9,
        TerminalModeChanged = 10,
        OscObserved = 11,
        OpenLink = 12,
        SwapChainChanged = 13,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TilSize
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TerminalTheme
    {
        internal uint DefaultBackground;
        internal uint DefaultForeground;
        internal uint DefaultSelectionBackground;
        internal uint CursorColor;
        internal uint CursorStyle;

        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U4, SizeConst = 16)]
        internal uint[] ColorTable;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TerminalOptions
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;
        internal uint Flags;
        internal uint DefaultBackground;
        internal uint DefaultForeground;
        internal uint DefaultSelectionBackground;
        internal uint CursorColor;
        internal uint CursorStyle;

        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U4, SizeConst = 16)]
        internal uint[] ColorTable;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string FontFamily;
        internal uint FontFamilyLength;
        internal short FontSize;
        internal ushort Reserved;
        internal int Dpi;
        internal uint InteractionFlags;
        internal uint CopyFormatting;
        internal uint PasteFiltering;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SearchRequest
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string Query;

        internal uint QueryLength;
        internal uint Flags;
        internal int ScrollOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSearchState
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;
        internal int TotalMatches;
        internal int CurrentMatch;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeHighlightRule
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;
        internal ulong Id;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string Pattern;

        internal uint PatternLength;
        internal uint Flags;
        internal uint Foreground;
        internal uint Background;
        internal int Priority;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeHighlightRow
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;
        internal int Row;
        internal uint Count;
        internal uint Color;
    }

    internal sealed record HighlightRulePayload(
        ulong Id,
        string Pattern,
        bool RegularExpression,
        bool MatchCase,
        bool ShowInOverview,
        uint Foreground,
        uint Background,
        int Priority);

    internal readonly record struct HighlightRowRecord(int Row, uint Count, uint Color);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMarkRecord
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;
        internal ulong Id;
        internal ulong Generation;
        internal uint Kind;
        internal uint Flags;
        internal uint Category;
        internal uint Color;
        internal int ExitCode;
        internal int StartX;
        internal int StartY;
        internal int PromptEndX;
        internal int PromptEndY;
        internal int CommandEndX;
        internal int CommandEndY;
        internal int OutputEndX;
        internal int OutputEndY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCursorLogicalLine
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;
        internal ulong ProbeId;
        internal ulong Generation;
        internal int StartRow;
        internal int CursorRow;
        internal int CursorColumn;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSnapshot
    {
        internal uint StructSize;
        internal ushort AbiMajor;
        internal ushort AbiMinor;
        internal uint SchemaVersion;
        internal uint Flags;
        internal int Columns;
        internal int Rows;
        internal int CursorColumn;
        internal int CursorRow;
        internal int ViewportTop;
        internal int ViewportHeight;
        internal int ScrollOffset;
        internal ulong CaptureSequence;
        internal long UnixTimeMilliseconds;
        internal uint AnsiOffset;
        internal uint AnsiLength;
        internal uint TitleOffset;
        internal uint TitleLength;
        internal uint WorkingDirectoryOffset;
        internal uint WorkingDirectoryLength;
    }

    internal readonly record struct NativeTerminalCreationSettings(
        int InitialColumns,
        int InitialRows,
        int HistorySize,
        string FontFamily,
        int FontSize,
        TerminalTheme Theme,
        bool CopyOnSelect,
        bool RightClickPaste,
        bool AllowOscClipboard,
        bool AllowOscNotifications,
        bool ReadOnly);

    internal readonly record struct SearchState(
        int TotalMatches,
        int CurrentMatch,
        bool Invalidated,
        bool InvalidRegex);

    internal enum MarkKind : uint
    {
        ExactCommand = 1,
        ApplicationCommand = 2,
        Bookmark = 3,
    }

    internal readonly record struct MarkRecord(
        ulong Id,
        ulong Generation,
        MarkKind Kind,
        uint Flags,
        uint Category,
        uint Color,
        int? ExitCode,
        int Row);

    internal readonly record struct PromptProbe(
        ulong Id,
        ulong Generation,
        int StartRow,
        int CursorRow,
        int CursorColumn,
        string Text);

    internal readonly record struct Snapshot(
        uint SchemaVersion,
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
        string Ansi,
        string Title,
        string WorkingDirectory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetAbiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBuildIdDelegate(IntPtr buffer, uint capacity, out uint requiredCapacity);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDelegate(in CreateOptions options, out IntPtr terminal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DestroyDelegate(IntPtr terminal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void EventCallback(IntPtr context, in NativeEvent eventData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RegisterEventCallbackDelegate(
        IntPtr terminal,
        EventCallback callback,
        IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SendOutputDelegate(
        IntPtr terminal,
        [MarshalAs(UnmanagedType.LPWStr)] string text,
        uint textLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SendKeyEventDelegate(
        IntPtr terminal,
        ushort virtualKey,
        ushort scanCode,
        ushort flags,
        byte keyDown,
        out byte handled);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SendCharEventDelegate(
        IntPtr terminal,
        char character,
        ushort scanCode,
        ushort flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFocusedDelegate(IntPtr terminal, byte focused);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResizeCharactersDelegate(IntPtr terminal, int columns, int rows);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetBoundsDelegate(
        IntPtr terminal,
        int screenX,
        int screenY,
        int width,
        int height,
        out int columns,
        out int rows);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AttachSwapChainPanelDelegate(IntPtr terminal, IntPtr panel);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SendPointerEventDelegate(
        IntPtr terminal,
        uint message,
        uint buttons,
        int x,
        int y,
        short wheelDelta,
        out uint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetOptionsDelegate(IntPtr terminal, in TerminalOptions options);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CopySelectionDelegate(IntPtr terminal, byte clearSelection);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int PasteTextDelegate(
        IntPtr terminal,
        [MarshalAs(UnmanagedType.LPWStr)] string text,
        uint textLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SearchDelegate(
        IntPtr terminal,
        in SearchRequest request,
        out NativeSearchState state);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ClearSearchDelegate(IntPtr terminal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSearchStateDelegate(IntPtr terminal, out NativeSearchState state);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UserScrollDelegate(IntPtr terminal, int viewTop);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMarksDelegate(
        IntPtr terminal,
        IntPtr records,
        uint capacity,
        out uint requiredCapacity);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSearchRowsDelegate(
        IntPtr terminal,
        IntPtr rows,
        uint capacity,
        out uint requiredCapacity);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMarkTextDelegate(
        IntPtr terminal,
        ulong markId,
        byte includeOutput,
        IntPtr buffer,
        uint capacity,
        out uint requiredCapacity);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ScrollToMarkDelegate(IntPtr terminal, ulong markId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCursorLogicalLineDelegate(
        IntPtr terminal,
        out NativeCursorLogicalLine line,
        IntPtr buffer,
        uint capacity,
        out uint requiredCapacity);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int CreateApplicationMarkDelegate(
        IntPtr terminal,
        ulong probeId,
        [MarshalAs(UnmanagedType.LPWStr)] string command,
        uint commandLength,
        int exitCode,
        byte hasExitCode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DiscardPromptProbeDelegate(IntPtr terminal, ulong probeId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AddBookmarkDelegate(
        IntPtr terminal,
        int row,
        uint color,
        byte hasColor,
        out ulong bookmarkId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RemoveBookmarkDelegate(IntPtr terminal, ulong bookmarkId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ClearBookmarksDelegate(IntPtr terminal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CaptureSnapshotDelegate(
        IntPtr terminal,
        ref NativeSnapshot snapshot,
        IntPtr payload,
        uint capacity,
        out uint requiredCapacity);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SetHighlightRulesDelegate(
        IntPtr terminal,
        [In] NativeHighlightRule[] rules,
        uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ClearHighlightRulesDelegate(IntPtr terminal);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetHighlightRowsDelegate(
        IntPtr terminal,
        IntPtr rows,
        uint capacity,
        out uint requiredCapacity);
}
