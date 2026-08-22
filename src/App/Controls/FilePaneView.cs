using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Resesh.Core.Local;
using Resesh.App.Interop;
using Resesh.Core.Models;
using Resesh.Core.Sftp;
using Resesh.Core.Ssh;
using Windows.ApplicationModel.DataTransfer;

namespace Resesh.App.Controls;

/// <summary>
/// The per-tab file pane. Local sessions use the Windows filesystem directly. SSH
/// sessions use a lazily connected SFTP channel and optional SSHFS Explorer access.
/// One operation runs at a time.
/// </summary>
public sealed class FilePaneView : UserControl, IDisposable
{
    private readonly Func<Session> _session;
    private readonly Func<Task<SftpSession>>? _connectFactory;
    private readonly Func<string, Task> _openInExplorer;
    private readonly LocalFileSystem? _localFiles;

    private readonly TextBox _pathBox = new()
    {
        PlaceholderText = "/",
        VerticalAlignment = VerticalAlignment.Center,
        IsSpellCheckEnabled = false,
    };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended };
    private readonly ProgressRing _loading = new() { IsActive = false, Width = 36, Height = 36 };
    private readonly TextBlock _emptyText = new()
    {
        Text = "Empty folder",
        Opacity = 0.55,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    private readonly StackPanel _errorPanel;
    private readonly TextBlock _errorText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly Grid _transferStrip;
    private readonly ProgressBar _transferBar = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _transferText = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 12,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly TextBlock _statusText = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(8, 2, 8, 6),
    };
    private readonly MenuFlyout _menu = new();
    private MenuFlyoutItem _open = null!, _download = null!, _downloadOpen = null!, _rename = null!,
        _permissions = null!, _delete = null!, _mkdir = null!, _uploadFiles = null!, _refresh = null!;

    private SftpSession? _sftp;
    private string _currentPath = "/";
    private bool _busy;
    private CancellationTokenSource? _opCts;
    private RemoteFileEntry? _menuEntry;
    private bool _disposed;

    public event Action? CloseRequested;

    public string CurrentPath => _currentPath;

    public FilePaneView(
        Func<Session> session, Func<Task<SftpSession>> connectFactory, Func<string, Task> openInExplorer)
        : this(session, connectFactory, openInExplorer, localFiles: null)
    {
    }

    public FilePaneView(Func<Session> session, Func<string, Task> openInExplorer)
        : this(session, connectFactory: null, openInExplorer,
            new LocalFileSystem(session().Local?.StartingDirectory))
    {
    }

    private FilePaneView(
        Func<Session> session,
        Func<Task<SftpSession>>? connectFactory,
        Func<string, Task> openInExplorer,
        LocalFileSystem? localFiles)
    {
        _session = session;
        _connectFactory = connectFactory;
        _openInExplorer = openInExplorer;
        _localFiles = localFiles;
        _pathBox.PlaceholderText = localFiles is null ? "/" : @"C:\";
        // Compact rows in the codebase's tree style (default ListViewItems are 40px tall).
        var itemStyle = new Style(typeof(ListViewItem));
        itemStyle.Setters.Add(new Setter(Control.MinHeightProperty, 26d));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 1, 8, 1)));
        // A custom ItemContainerStyle drops the theme default's Stretch — without it each
        // row grid collapses to its content width and the columns go ragged.
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _list.ItemContainerStyle = itemStyle;
        _list.RightTapped += List_RightTapped;
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Back)
            {
                e.Handled = true;
                _ = NavigateAsync(ParentPath(_currentPath));
            }
        };

        _errorPanel = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 280,
            Visibility = Visibility.Collapsed,
        };
        var retry = new Button { Content = "Retry", HorizontalAlignment = HorizontalAlignment.Center };
        // Before the first successful listing there is no meaningful current path — retry to home.
        retry.Click += (_, _) => _ = NavigateAsync(_list.Items.Count == 0 ? null : _currentPath);
        _errorPanel.Children.Add(_errorText);
        _errorPanel.Children.Add(retry);

        var cancel = IconButton("", "Cancel transfer", () => _opCts?.Cancel());
        _transferStrip = new Grid { Padding = new Thickness(8, 4, 8, 4), Visibility = Visibility.Collapsed, ColumnSpacing = 8 };
        _transferStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _transferStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var transferInfo = new StackPanel { Spacing = 2 };
        transferInfo.Children.Add(_transferText);
        transferInfo.Children.Add(_transferBar);
        _transferStrip.Children.Add(transferInfo);
        Grid.SetColumn(cancel, 1);
        _transferStrip.Children.Add(cancel);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(BuildToolbar());

        var listHost = new Grid();
        listHost.Children.Add(_list);
        listHost.Children.Add(_emptyText);
        listHost.Children.Add(_loading);
        listHost.Children.Add(_errorPanel);
        Grid.SetRow(listHost, 1);
        root.Children.Add(listHost);

        Grid.SetRow(_transferStrip, 2);
        root.Children.Add(_transferStrip);
        Grid.SetRow(_statusText, 3);
        root.Children.Add(_statusText);

        Content = new Border
        {
            Child = root,
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = ThemeBrush("DividerStrokeColorDefaultBrush"),
            Background = ThemeBrush("LayerFillColorDefaultBrush"),
        };

        BuildMenu();

        // Remote panes accept Explorer drag-in as uploads. Local panes already display
        // the same filesystem, so a drop must not copy a path onto itself.
        if (_localFiles is null)
        {
            AllowDrop = true;
            DragOver += (_, e) =>
            {
                if (e.DataView.Contains(StandardDataFormats.StorageItems) && !_busy)
                {
                    e.AcceptedOperation = DataPackageOperation.Copy;
                    e.DragUIOverride.Caption = $"Upload to {_currentPath}";
                }
            };
            Drop += Pane_Drop;
        }

        Loaded += (_, _) =>
        {
            if (_list.Items.Count == 0)
                _ = NavigateAsync(null);
        };
    }

    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80));

    private UIElement BuildToolbar()
    {
        var bar = new Grid { Padding = new Thickness(4), ColumnSpacing = 2 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var up = IconButton("", "Up one folder", () => _ = NavigateAsync(ParentPath(_currentPath)));
        var home = IconButton("", "Home folder", () => _ = NavigateAsync(null));
        Grid.SetColumn(home, 1);
        _pathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && !string.IsNullOrWhiteSpace(_pathBox.Text))
            {
                e.Handled = true;
                _ = NavigateAsync(_pathBox.Text.Trim());
            }
        };
        Grid.SetColumn(_pathBox, 2);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        actions.Children.Add(IconButton("", "Refresh", () => _ = NavigateAsync(_currentPath)));
        actions.Children.Add(IconButton("", "New folder", () => _ = CreateFolderAsync()));
        if (_localFiles is null)
            actions.Children.Add(IconButton("", "Upload files…", () => _ = PickAndUploadAsync()));
        if (_localFiles is not null || SshfsIntegration.IsInstalled)
        {
            var tooltip = _localFiles is null ? "Open in Explorer (SSHFS-Win)" : "Open in Explorer";
            actions.Children.Add(IconButton("", tooltip, () => _ = OpenInExplorerAsync()));
        }
        actions.Children.Add(IconButton("", "Close file pane", () => CloseRequested?.Invoke()));
        Grid.SetColumn(actions, 3);

        bar.Children.Add(up);
        bar.Children.Add(home);
        bar.Children.Add(_pathBox);
        bar.Children.Add(actions);
        return bar;
    }

    private static Button IconButton(string glyph, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(7),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    // ---- context menu ----

    private void BuildMenu()
    {
        MenuFlyoutItem Item(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            return item;
        }

        _open = Item("Open", () =>
        {
            if (_menuEntry is { } entry)
                OpenEntry(entry);
        });
        _download = Item("Download…", () => _ = DownloadSelectionAsync());
        _downloadOpen = Item("Download && Open", () => _ = DownloadAndOpenAsync());
        _rename = Item("Rename…", () => _ = RenameAsync());
        _permissions = Item("Permissions…", () => _ = ChangePermissionsAsync());
        _delete = Item("Delete…", () => _ = DeleteSelectionAsync());
        _mkdir = Item("New Folder…", () => _ = CreateFolderAsync());
        _uploadFiles = Item("Upload Files Here…", () => _ = PickAndUploadAsync());
        _refresh = Item("Refresh", () => _ = NavigateAsync(_currentPath));

        _menu.Items.Add(_open);
        if (_localFiles is null)
        {
            _menu.Items.Add(_download);
            _menu.Items.Add(_downloadOpen);
        }
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(_rename);
        if (_localFiles is null)
            _menu.Items.Add(_permissions);
        _menu.Items.Add(_delete);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(_mkdir);
        if (_localFiles is null)
            _menu.Items.Add(_uploadFiles);
        _menu.Items.Add(_refresh);
    }

    private void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var entry = EntryFromOriginalSource(e.OriginalSource);
        _menuEntry = entry;
        if (entry is not null && !SelectedEntries().Contains(entry))
        {
            _list.SelectedItems.Clear();
            foreach (var item in _list.Items)
            {
                if (ReferenceEquals((item as FrameworkElement)?.Tag, entry))
                    _list.SelectedItems.Add(item);
            }
        }

        var selection = SelectedEntries();
        var single = selection.Count == 1 ? selection[0] : null;
        _open.Visibility = single is not null && (_localFiles is not null || single.IsDirectory)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _download.IsEnabled = selection.Count > 0 && !_busy;
        _downloadOpen.Visibility = single is { IsDirectory: false } ? Visibility.Visible : Visibility.Collapsed;
        _downloadOpen.IsEnabled = !_busy;
        _rename.IsEnabled = single is not null && !_busy;
        _permissions.IsEnabled = single is not null && !_busy;
        _delete.IsEnabled = selection.Count > 0 && !_busy;
        _mkdir.IsEnabled = !_busy;
        _uploadFiles.IsEnabled = !_busy;
        _refresh.IsEnabled = !_busy;

        _menu.ShowAt(_list, e.GetPosition(_list));
        e.Handled = true;
    }

    private static RemoteFileEntry? EntryFromOriginalSource(object originalSource)
    {
        for (var d = originalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { Tag: RemoteFileEntry entry })
                return entry;
        }
        return null;
    }

    private List<RemoteFileEntry> SelectedEntries() =>
        _list.SelectedItems
            .OfType<FrameworkElement>()
            .Select(e => e.Tag)
            .OfType<RemoteFileEntry>()
            .ToList();

    private string ParentPath(string path)
    {
        if (_localFiles is null)
            return RemotePath.Parent(path);
        return Directory.GetParent(path)?.FullName ?? path;
    }

    private void OpenEntry(RemoteFileEntry entry)
    {
        if (_localFiles is null || entry.IsDirectory || Directory.Exists(entry.FullPath))
        {
            _ = NavigateAsync(entry.FullPath);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.FullPath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            ShowStatus($"Open failed: {ex.Message}", isError: true);
        }
    }

    /// <summary>Opens the current local directory directly, or mounts the remote directory
    /// through SSHFS before opening it.</summary>
    private async Task OpenInExplorerAsync()
    {
        ShowStatus(_localFiles is null ? "Connecting Explorer view (sshfs)…" : "Opening Explorer…", isError: false);
        try
        {
            await _openInExplorer(_currentPath);
            ShowStatus("Opened in Explorer.", isError: false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            ShowStatus($"Explorer view failed: {ex.Message}", isError: true);
        }
    }

    // ---- connection + navigation ----

    /// <summary>Navigates to a directory (null = profile home) and refreshes the listing.
    /// A notice replaces the item-count status after a successful fallback.</summary>
    public async Task NavigateAsync(string? path, string? notice = null)
    {
        if (_busy || _disposed)
            return;
        _busy = true;
        _loading.IsActive = true;
        _errorPanel.Visibility = Visibility.Collapsed;
        try
        {
            string target;
            IReadOnlyList<RemoteFileEntry> entries;
            if (_localFiles is { } localFiles)
            {
                target = localFiles.ResolveDirectory(path, _currentPath);
                entries = await Task.Run(() => localFiles.ListDirectory(target));
            }
            else
            {
                var sftp = await EnsureConnectedAsync();
                target = RemotePath.ResolveShellPath(path, sftp.HomeDirectory) ??
                    throw new InvalidOperationException("The terminal did not report an absolute remote path.");
                entries = await Task.Run(() => sftp.ListDirectory(target));
            }
            _currentPath = target;
            _pathBox.Text = target;
            PopulateList(entries);
            if (notice is not null)
                ShowStatus(notice, isError: true);
            else
                ShowStatus($"{entries.Count} item{(entries.Count == 1 ? "" : "s")}", isError: false);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // A failed navigation into a subfolder keeps the old listing; a failed
            // initial load shows the retry overlay.
            if (_list.Items.Count == 0)
            {
                _errorText.Text = ex.Message;
                _errorPanel.Visibility = Visibility.Visible;
            }
            else
            {
                ShowStatus(ex.Message, isError: true);
            }
        }
        finally
        {
            _busy = false;
            _loading.IsActive = false;
        }
    }

    private async Task<SftpSession> EnsureConnectedAsync()
    {
        if (_sftp?.IsConnected == true)
            return _sftp;
        _sftp?.Dispose();
        _sftp = null;
        _sftp = await (_connectFactory?.Invoke()
            ?? throw new InvalidOperationException("This pane has no SFTP connection factory."));
        return _sftp;
    }

    private void PopulateList(IReadOnlyList<RemoteFileEntry> entries)
    {
        _list.Items.Clear();
        foreach (var entry in entries)
            _list.Items.Add(BuildRow(entry));
        _emptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Grid BuildRow(RemoteFileEntry entry)
    {
        var row = new Grid { ColumnSpacing = 8, Tag = entry, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });

        var icon = new FontIcon
        {
            Glyph = entry.IsSymlink ? "" : entry.IsDirectory ? "" : "",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var name = new TextBlock
        {
            Text = entry.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var details = _localFiles is null
            ? $"{entry.PermissionText}  {entry.Modified:yyyy-MM-dd HH:mm}"
            : entry.Modified.ToString("yyyy-MM-dd HH:mm");
        ToolTipService.SetToolTip(row, $"{entry.Name}\n{details}");
        var size = new TextBlock
        {
            Text = entry.IsDirectory ? "" : FormatSize(entry.Size),
            Opacity = 0.55,
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var modified = new TextBlock
        {
            Text = entry.Modified.ToString("yyyy-MM-dd HH:mm"),
            Opacity = 0.55,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(size, 2);
        Grid.SetColumn(modified, 3);
        row.Children.Add(icon);
        row.Children.Add(name);
        row.Children.Add(size);
        row.Children.Add(modified);

        row.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            if (_localFiles is not null)
                OpenEntry(entry);
            else if (entry.IsDirectory || entry.IsSymlink)
                _ = NavigateAsync(entry.FullPath); // symlink-to-file navigation fails with a status line
            else
                _ = DownloadAndOpenAsync(entry);
        };
        return row;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    // ---- operations (one at a time; transfers show the progress strip) ----

    private async Task RunOperationAsync(string failurePrefix, Func<SftpSession, CancellationToken, Task> work, bool refreshAfter = true)
    {
        if (_busy || _disposed)
        {
            ShowStatus("Another file operation is still running.", isError: true);
            return;
        }
        _busy = true;
        _opCts = new CancellationTokenSource();
        try
        {
            var sftp = await EnsureConnectedAsync();
            await work(sftp, _opCts.Token);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Cancelled.", isError: false);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowStatus($"{failurePrefix}: {ex.Message}", isError: true);
        }
        finally
        {
            _busy = false;
            _opCts.Dispose();
            _opCts = null;
            _transferStrip.Visibility = Visibility.Collapsed;
        }
        if (refreshAfter && !_disposed)
            await NavigateAsync(_currentPath);
    }

    private static bool IsExpected(Exception ex) => ex is SshSessionException
        or Renci.SshNet.Common.SshException or IOException or UnauthorizedAccessException
        or InvalidOperationException or ObjectDisposedException;

    private void ReportTransfer(string verb, string name, int index, int count, long done, long total)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _transferStrip.Visibility = Visibility.Visible;
            var position = count > 1 ? $" ({index}/{count})" : "";
            if (total > 0)
            {
                _transferBar.IsIndeterminate = false;
                _transferBar.Maximum = total;
                _transferBar.Value = Math.Min(done, total);
                _transferText.Text = $"{verb} {name}{position} — {FormatSize(done)} / {FormatSize(total)}";
            }
            else
            {
                _transferBar.IsIndeterminate = true;
                _transferText.Text = $"{verb} {name}{position}";
            }
        });
    }

    private void ShowStatus(string message, bool isError)
    {
        _statusText.Text = message;
        _statusText.Opacity = isError ? 1.0 : 0.55;
        _statusText.Foreground = isError
            ? ThemeBrush("SystemFillColorCriticalBrush")
            : ThemeBrush("TextFillColorPrimaryBrush");
        _statusText.Visibility = Visibility.Visible;
    }

    // ---- uploads ----

    private async Task PickAndUploadAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle());
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
            await UploadPathsAsync(files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList());
    }

    private async void Pane_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count < items.Count)
                ShowStatus("Some dropped items have no filesystem path and were skipped.", isError: true);
            if (paths.Count > 0)
                await UploadPathsAsync(paths);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Uploads local files/folders (recursively) into the current directory.</summary>
    private async Task UploadPathsAsync(IReadOnlyList<string> localPaths)
    {
        var target = _currentPath;
        await RunOperationAsync("Upload failed", async (sftp, token) =>
        {
            // Plan first so progress can show a stable total, then confirm overwrites once.
            var files = new List<(string Local, string Remote, long Size)>();
            var directories = new List<string>();
            foreach (var local in localPaths)
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(local));
                if (Directory.Exists(local))
                    PlanLocalDirectory(local, RemotePath.Join(target, name), files, directories);
                else if (File.Exists(local))
                    files.Add((local, RemotePath.Join(target, name), new FileInfo(local).Length));
            }
            if (files.Count == 0 && directories.Count == 0)
                return;

            var existing = await Task.Run(() => sftp.ListDirectory(target), token);
            var taken = existing.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            var clashes = files.Count(f => RemotePath.Parent(f.Remote) == target && taken.Contains(RemotePath.FileName(f.Remote)));
            if (clashes > 0 && !await ConfirmAsync(
                    "Overwrite Files",
                    $"{clashes} file{(clashes == 1 ? " already exists" : "s already exist")} in {target} and will be overwritten.",
                    "Overwrite"))
                return;

            var index = 0;
            await Task.Run(() =>
            {
                foreach (var dir in directories)
                {
                    token.ThrowIfCancellationRequested();
                    if (!sftp.DirectoryExists(dir))
                        sftp.CreateDirectory(dir);
                }
                foreach (var (local, remote, sizeBytes) in files)
                {
                    token.ThrowIfCancellationRequested();
                    index++;
                    var name = RemotePath.FileName(remote);
                    ReportTransfer("Uploading", name, index, files.Count, 0, sizeBytes);
                    sftp.Upload(local, remote, done => ReportTransfer("Uploading", name, index, files.Count, done, sizeBytes), token);
                }
            }, token);
            ShowStatus($"Uploaded {files.Count} file{(files.Count == 1 ? "" : "s")}.", isError: false);
        });
    }

    private static void PlanLocalDirectory(
        string localDir, string remoteDir, List<(string, string, long)> files, List<string> directories)
    {
        directories.Add(remoteDir);
        foreach (var dir in Directory.EnumerateDirectories(localDir))
            PlanLocalDirectory(dir, RemotePath.Join(remoteDir, Path.GetFileName(dir)), files, directories);
        foreach (var file in Directory.EnumerateFiles(localDir))
            files.Add((file, RemotePath.Join(remoteDir, Path.GetFileName(file)), new FileInfo(file).Length));
    }

    // ---- downloads ----

    private async Task DownloadSelectionAsync()
    {
        var selection = SelectedEntries();
        if (selection.Count == 0)
            return;
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle());
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;
        await DownloadEntriesAsync(selection, folder.Path, openAfter: false);
    }

    private async Task DownloadAndOpenAsync(RemoteFileEntry? entry = null)
    {
        entry ??= SelectedEntries().FirstOrDefault(e => !e.IsDirectory);
        if (entry is null || entry.IsDirectory)
            return;
        // Per-download temp folder so simultaneous "open" of same-named files never collide.
        var tempDir = Path.Combine(Path.GetTempPath(), "Resesh", "opened", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        await DownloadEntriesAsync([entry], tempDir, openAfter: true);
    }

    private async Task DownloadEntriesAsync(IReadOnlyList<RemoteFileEntry> entries, string targetDir, bool openAfter)
    {
        await RunOperationAsync("Download failed", async (sftp, token) =>
        {
            // Plan the full file list up front (recursing into directories) for stable progress.
            var files = new List<(RemoteFileEntry Entry, string Local)>();
            var openTargets = new List<string>();
            await Task.Run(() =>
            {
                foreach (var entry in entries)
                {
                    var localName = RemotePath.UniqueName(
                        entry.Name,
                        name => File.Exists(Path.Combine(targetDir, name)) || Directory.Exists(Path.Combine(targetDir, name)));
                    var localPath = Path.Combine(targetDir, localName);
                    if (entry.IsDirectory && !entry.IsSymlink)
                        PlanRemoteDirectory(sftp, entry.FullPath, localPath, files, token);
                    else
                        files.Add((entry, localPath));
                    if (openAfter)
                        openTargets.Add(localPath);
                }

                var index = 0;
                foreach (var (entry, local) in files)
                {
                    token.ThrowIfCancellationRequested();
                    index++;
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    ReportTransfer("Downloading", entry.Name, index, files.Count, 0, entry.Size);
                    sftp.Download(
                        entry.FullPath, local,
                        done => ReportTransfer("Downloading", entry.Name, index, files.Count, done, entry.Size),
                        token);
                }
            }, token);

            ShowStatus($"Downloaded {files.Count} file{(files.Count == 1 ? "" : "s")} to {targetDir}", isError: false);
            foreach (var path in openTargets)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }, refreshAfter: false);
    }

    private static void PlanRemoteDirectory(
        SftpSession sftp, string remoteDir, string localDir,
        List<(RemoteFileEntry, string)> files, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(localDir);
        foreach (var child in sftp.ListDirectory(remoteDir))
        {
            if (child.IsDirectory && !child.IsSymlink)
                PlanRemoteDirectory(sftp, child.FullPath, Path.Combine(localDir, child.Name), files, token);
            else
                files.Add((child, Path.Combine(localDir, child.Name)));
        }
    }

    private async Task RunLocalOperationAsync(string failurePrefix, Action<LocalFileSystem> work)
    {
        if (_busy || _disposed || _localFiles is not { } localFiles)
            return;
        _busy = true;
        try
        {
            await Task.Run(() => work(localFiles));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowStatus($"{failurePrefix}: {ex.Message}", isError: true);
        }
        finally
        {
            _busy = false;
        }
        if (!_disposed)
            await NavigateAsync(_currentPath);
    }

    private bool IsInvalidName(string name) =>
        _localFiles is null ? name.Contains('/') : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;

    // ---- rename / delete / mkdir / chmod ----

    private async Task RenameAsync()
    {
        if (SelectedEntries() is not [{ } entry])
            return;
        var newName = await PromptAsync("Rename", "New name", entry.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name)
            return;
        if (IsInvalidName(newName))
        {
            ShowStatus("The name contains an invalid character.", isError: true);
            return;
        }
        var trimmed = newName.Trim();
        if (_localFiles is not null)
        {
            await RunLocalOperationAsync("Rename failed", files =>
                files.Rename(entry, Path.Combine(ParentPath(entry.FullPath), trimmed)));
            return;
        }
        await RunOperationAsync("Rename failed", (sftp, _) =>
            Task.Run(() => sftp.Rename(entry.FullPath, RemotePath.Join(RemotePath.Parent(entry.FullPath), trimmed))));
    }

    private async Task DeleteSelectionAsync()
    {
        var selection = SelectedEntries();
        if (selection.Count == 0)
            return;
        var what = selection.Count == 1
            ? $"\"{selection[0].Name}\""
            : $"{selection.Count} items";
        var isLocal = _localFiles is not null;
        var title = isLocal ? "Delete Local Files" : "Delete From Server";
        var location = isLocal ? _currentPath : _session().Host;
        if (!await ConfirmAsync(
                title,
                $"Permanently delete {what} from {location}? Folders are deleted with everything in them. This cannot be undone."))
            return;
        if (isLocal)
        {
            await RunLocalOperationAsync("Delete failed", files =>
            {
                foreach (var entry in selection)
                    files.Delete(entry);
            });
            return;
        }
        await RunOperationAsync("Delete failed", (sftp, token) => Task.Run(() =>
        {
            foreach (var entry in selection)
                sftp.Delete(entry, token);
        }, token));
    }

    private async Task CreateFolderAsync()
    {
        var name = await PromptAsync("New Folder", "Folder name", "");
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (IsInvalidName(name))
        {
            ShowStatus("The name contains an invalid character.", isError: true);
            return;
        }
        var trimmed = name.Trim();
        if (_localFiles is not null)
        {
            await RunLocalOperationAsync("Create folder failed", files =>
                files.CreateDirectory(Path.Combine(_currentPath, trimmed)));
            return;
        }
        var path = RemotePath.Join(_currentPath, trimmed);
        await RunOperationAsync("Create folder failed", (sftp, _) => Task.Run(() => sftp.CreateDirectory(path)));
    }

    private async Task ChangePermissionsAsync()
    {
        if (SelectedEntries() is not [{ } entry])
            return;
        var current = entry.Mode >= 0 ? entry.Mode.ToString() : "";
        var input = await PromptAsync(
            $"Permissions — {entry.Name}",
            "Octal mode, e.g. 644 or 755",
            current);
        if (input is null || input.Trim() == current)
            return;
        if (!UnixPermissions.TryParseOctal(input, out var mode))
        {
            ShowStatus($"\"{input.Trim()}\" is not a valid octal mode (expected e.g. 644).", isError: true);
            return;
        }
        await RunOperationAsync("Permission change failed", (sftp, _) =>
            Task.Run(() => sftp.ChangePermissions(entry.FullPath, mode)));
    }

    // ---- local dialog helpers (the pane lives inside a tab, not a window) ----

    private IntPtr WindowHandle() =>
        Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);

    private async Task<string?> PromptAsync(string title, string placeholder, string initial)
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initial };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText = "Delete")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _opCts?.Cancel();
        var sftp = _sftp;
        _sftp = null;
        if (sftp is not null)
            _ = Task.Run(sftp.Dispose); // network teardown off the UI thread
    }
}
