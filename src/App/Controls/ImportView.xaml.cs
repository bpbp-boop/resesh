using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Resesh.Core.Identities;
using Resesh.Core.Import;
using Resesh.Core.Storage;

namespace Resesh.App.Controls;

/// <summary>What an import did, for whoever opened the review.</summary>
public sealed record ImportOutcome(int Imported, int Duplicates, IReadOnlyList<string> CreatedIdentities);

/// <summary>A row in the review list: a folder or a session, indented by its depth.</summary>
public abstract partial class ImportRow : ObservableObject
{
    public int Depth { get; set; }

    public Thickness Indent => new(Depth * 24, 0, 0, 0);
}

/// <summary>One importable session in the review list.</summary>
public sealed partial class ImportCandidateVm : ImportRow
{
    private bool _isSelected = true;
    private string _loginDetail = "";

    public ImportCandidate Candidate { get; init; } = new();

    /// <summary>False for sessions that already exist; they are skipped either way.</summary>
    public bool CanImport { get; init; } = true;

    public string FolderPath => FolderPaths.Normalize(Candidate.FolderPath);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>How the session will log in after the import.</summary>
    public string LoginDetail
    {
        get => _loginDetail;
        set
        {
            if (SetProperty(ref _loginDetail, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public string Name => Candidate.Name;

    public string Endpoint
    {
        get
        {
            if (Candidate.IsTelnet)
                return $"telnet {Candidate.Host}" + (Candidate.Port == 23 ? "" : $":{Candidate.Port}");
            if (Candidate.Serial is { } serial)
                return $"serial {serial.Describe()}";
            var target = Candidate.Port == 22 ? Candidate.Host : $"{Candidate.Host}:{Candidate.Port}";
            var user = Candidate.Username.Length > 0 ? $"{Candidate.Username}@" : "";
            var key = Candidate.PrivateKeyPath is { Length: > 0 } path ? $" · key {Path.GetFileName(path)}" : "";
            return $"{user}{target}{key}";
        }
    }

    public string EndpointTip => Candidate.PrivateKeyPath is { Length: > 0 } path
        ? $"{Endpoint}\nSSH key: {path}" + (File.Exists(path) ? "" : " (file not found)")
        : Endpoint;

    public string AccessibleName => $"{Name}, {Endpoint}, {LoginDetail}";

    public bool Matches(string filter) => filter.Length == 0
        || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Candidate.Host.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Candidate.Username.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Candidate.FolderPath.Contains(filter, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A folder in the review list. Its checkbox covers every session below it,
/// subfolders included; its chevron shows or hides what is inside.</summary>
public sealed partial class ImportFolderRow : ImportRow
{
    private readonly ImportView _owner;

    public ImportFolderRow(ImportView owner, string path, IReadOnlyList<ImportCandidateVm> subtree)
    {
        _owner = owner;
        Path = path;
        Subtree = subtree;
    }

    public string Path { get; }
    public string Name => FolderPaths.Name(Path);
    public IReadOnlyList<ImportCandidateVm> Subtree { get; }

    public bool IsExpanded => _owner.IsExpanded(Path);
    public string Chevron => IsExpanded ? "" : "";
    public string ToggleName => $"{(IsExpanded ? "Collapse" : "Expand")} {Path}";

    public string CountText
    {
        get
        {
            var selected = Subtree.Count(c => c.IsSelected);
            return selected == Subtree.Count ? $"{Subtree.Count}" : $"{selected} of {Subtree.Count}";
        }
    }

    public string Header => $"{Path}, {CountText} sessions";

    public bool? AllSelected
    {
        get => Subtree.Where(c => c.CanImport).All(c => c.IsSelected);
        set => _owner.SetSelected(Subtree.Where(c => c.CanImport), value == true);
    }

    public void Toggle() => _owner.ToggleFolder(Path);

    internal void NotifySelection()
    {
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(Header));
    }
}

/// <summary>Picks the folder or session template for a review row.</summary>
public sealed partial class ImportRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FolderTemplate { get; set; }
    public DataTemplate? SessionTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is ImportFolderRow ? FolderTemplate : SessionTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}

/// <summary>
/// Reviews sessions found by an importer in a tab: which to import and which repeated logins
/// become shared identities. Closing the tab cancels the import.
/// </summary>
public sealed partial class ImportView : UserControl, IDisposable
{
    private readonly List<ImportCandidateVm> _candidates;
    private readonly HashSet<string> _declined = [];
    private readonly HashSet<string> _seen = [];
    private readonly TaskCompletionSource<ImportOutcome?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<ImportFolderRow> _folders = [];
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private ImportIdentityPlan _plan = ImportIdentityPlan.Empty;
    private bool _bulkChange;

    /// <summary>Completes with the outcome after importing, or null when closed without importing.</summary>
    public Task<ImportOutcome?> Completion => _completion.Task;

    public event Action? CloseRequested;
    public event Action? ManageIdentitiesRequested;

    public ImportView(ImportScanResult scan, string sourceName)
    {
        _candidates = scan.Importable
            .Select(c => new ImportCandidateVm { Candidate = c, CanImport = !SecureCrtImporter.IsDuplicate(App.Store, c) })
            .ToList();
        foreach (var candidate in _candidates)
        {
            if (!candidate.CanImport)
                candidate.IsSelected = false;
            candidate.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ImportCandidateVm.IsSelected) && !_bulkChange)
                    SelectionChanged();
            };
        }
        InitializeComponent();

        TitleText.Text = $"Import from {sourceName}";
        var folders = _candidates.Select(c => FolderPaths.Normalize(c.Candidate.FolderPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var existing = _candidates.Count(c => !c.CanImport);
        SummaryText.Text = $"Found {Count(_candidates.Count, "SSH or telnet session")} in {Count(folders, "folder")}."
            + (existing > 0 ? $" {Count(existing, "session")} already exist and will be skipped." : "")
            + (scan.Skipped.Count > 0 ? $" {Count(scan.Skipped.Count, "session")} of other types can't be imported." : "");
        if (scan.Skipped.Count > 0)
        {
            SkippedExpander.Header = $"{Count(scan.Skipped.Count, "session")} skipped (not SSH or telnet)";
            SkippedExpander.Visibility = Visibility.Visible;
            SkippedList.ItemsSource = scan.Skipped
                .Select(c => $"{c.RelativePath} — {(c.Protocol.Length > 0 ? c.Protocol : "unknown protocol")}")
                .ToList();
        }

        RebuildRows();
        SelectionChanged();
    }

    public void Dispose() => _completion.TrySetResult(null);

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    internal void SetSelected(IEnumerable<ImportCandidateVm> candidates, bool selected)
    {
        _bulkChange = true;
        try
        {
            foreach (var candidate in candidates)
                candidate.IsSelected = selected;
        }
        finally
        {
            _bulkChange = false;
        }
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        Replan();
        foreach (var folder in _folders)
            folder.NotifySelection();
        var selected = _candidates.Count(c => c.IsSelected);
        SelectionText.Text = $"{selected} of {_candidates.Count(c => c.CanImport)} selected";
        ImportButton.Content = selected == 1 ? "Import 1 session" : $"Import {selected} sessions";
        ImportButton.IsEnabled = selected > 0;
        var creating = _plan.Suggestions.Where(s => s.Existing is null).Select(s => s.Name).ToList();
        var reusing = _plan.Suggestions.Count(s => s.Existing is not null);
        var named = string.Join(", ", creating.Take(4)) + (creating.Count > 4 ? $" and {creating.Count - 4} more" : "");
        FooterText.Text = (creating.Count, reusing) switch
        {
            (0, 0) => "Every session keeps its own login.",
            (0, 1) => "Uses 1 existing identity.",
            (0, _) => $"Uses {reusing} existing identities.",
            _ => $"Creates {(creating.Count == 1 ? "1 identity" : $"{creating.Count} identities")}: {named}."
                + (reusing > 0 ? $" Uses {(reusing == 1 ? "1 existing identity" : $"{reusing} existing identities")}." : ""),
        };
    }

    internal bool IsExpanded(string folderPath) => !_collapsed.Contains(folderPath);

    internal void ToggleFolder(string folderPath)
    {
        if (!_collapsed.Remove(folderPath))
            _collapsed.Add(folderPath);
        RebuildRows();
    }

    /// <summary>
    /// Flattens the folder tree into rows: each folder, then (when expanded) its own sessions
    /// and its subfolders, indented by depth. Parent folders appear even when they hold no
    /// sessions of their own. Top-level sessions come first, without a folder row.
    /// </summary>
    private void RebuildRows()
    {
        var filter = FilterBox.Text.Trim();
        var visible = _candidates.Where(c => c.Matches(filter)).ToList();
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in visible)
        {
            foreach (var folder in FolderPaths.SelfAndAncestors(candidate.FolderPath))
                folders.Add(folder);
        }

        var rows = new List<ImportRow>();
        _folders = [];
        void AddSessions(string folder, int depth)
        {
            foreach (var candidate in visible
                         .Where(c => c.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                candidate.Depth = depth;
                rows.Add(candidate);
            }
        }
        void AddFolder(string folder, int depth)
        {
            var row = new ImportFolderRow(this, folder,
                visible.Where(c => FolderPaths.IsSelfOrDescendant(c.FolderPath, folder)).ToList()) { Depth = depth };
            rows.Add(row);
            _folders.Add(row);
            if (!IsExpanded(folder))
                return;
            foreach (var child in folders.Where(f => FolderPaths.Parent(f).Equals(folder, StringComparison.OrdinalIgnoreCase)))
                AddFolder(child, depth + 1);
            AddSessions(folder, depth + 1);
        }

        AddSessions("", 0);
        foreach (var top in folders.Where(f => FolderPaths.Parent(f).Length == 0))
            AddFolder(top, 0);
        CandidateList.ItemsSource = rows;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildRows();

    // Bulk selection acts on what the filter shows, collapsed folders included.
    private IEnumerable<ImportCandidateVm> Filtered =>
        _candidates.Where(c => c.Matches(FilterBox.Text.Trim()));

    private void SelectAll_Click(object sender, RoutedEventArgs e) =>
        SetSelected(Filtered.Where(c => c.CanImport), true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) =>
        SetSelected(Filtered, false);

    /// <summary>Plans identities for the selected sessions and shows the result.</summary>
    private void Replan()
    {
        var selected = _candidates.Where(c => c.IsSelected).Select(c => c.Candidate).ToList();
        // Every candidate login gets a row, declined or not, so it can be turned back on.
        var all = ImportIdentityPlanner.Plan(selected, App.Identities.Identities, App.Store.FolderIdentity,
            id => App.SshKeys.Find(id)?.Path);
        foreach (var suggestion in all.Suggestions)
        {
            // First sight decides the default: shared only when one secret is likely.
            if (_seen.Add(suggestion.Login.Key) && !suggestion.LikelyShared)
                _declined.Add(suggestion.Login.Key);
        }
        _plan = ImportIdentityPlanner.Plan(selected, App.Identities.Identities, App.Store.FolderIdentity,
            id => App.SshKeys.Find(id)?.Path, _declined);
        ShowSuggestions(all.Suggestions);
        foreach (var candidate in _candidates)
            candidate.LoginDetail = LoginText(candidate);
    }

    private void ShowSuggestions(IReadOnlyList<IdentitySuggestion> suggestions)
    {
        SuggestionRows.Children.Clear();
        LoginsIntro.Text = suggestions.Count == 0
            ? "No login repeats across the selected sessions, so each keeps its own username. You can attach "
              + "an identity to a folder later: right-click it in the session tree and choose Set Identity."
            : "Each ticked login becomes one identity. You type its password once and every session using it "
              + "shares it. Sessions in the listed folders inherit it, and so will sessions you add there later.";
        foreach (var suggestion in suggestions)
            SuggestionRows.Children.Add(SuggestionCard(suggestion));
    }

    private UIElement SuggestionCard(IdentitySuggestion suggestion)
    {
        var key = suggestion.Login.Key;
        var planned = _plan.Suggestions.FirstOrDefault(s => s.Login.Key == key) ?? suggestion;
        var ifShared = _declined.Contains(key) ? "If shared: " : "";
        var title = suggestion.Existing is not null ? $"Use {suggestion.Name}" : suggestion.Name;
        var proofs = new List<string>();
        if (suggestion.Existing is not null)
            proofs.Add("existing identity");
        else if (suggestion.Login.UsesKey)
            proofs.Add($"key {Path.GetFileName(suggestion.Login.KeyPath)}");
        proofs.Add(suggestion.Login.SharesSavedPassword ? "same saved password"
            : suggestion.Login.UsesKey || suggestion.Existing is not null ? "" : "one password");
        var proof = string.Join(" · ", proofs.Where(p => p.Length > 0));
        var lines = new StackPanel { Spacing = 2 };
        lines.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var user = suggestion.Name.Equals(suggestion.Login.Username, StringComparison.OrdinalIgnoreCase)
            ? "" : $"{suggestion.Login.Username} · ";
        lines.Children.Add(Muted($"{user}{proof} · {Count(planned.SessionCount, "session")}"));
        if (planned.Folders.Count > 0)
        {
            var shown = string.Join(", ", planned.Folders.Take(5));
            var more = planned.Folders.Count > 5 ? $" and {planned.Folders.Count - 5} more" : "";
            var folders = Muted($"{ifShared}{(ifShared.Length > 0 ? "inherited" : "Inherited")} in {shown}{more}");
            folders.TextWrapping = TextWrapping.Wrap;
            ToolTipService.SetToolTip(folders, string.Join("\n", planned.Folders));
            lines.Children.Add(folders);
        }
        else
            lines.Children.Add(Muted($"{ifShared}{(ifShared.Length > 0 ? "chosen" : "Chosen")} by each session (no folder where it's the main login)"));
        if (!suggestion.LikelyShared)
        {
            var caution = Muted("Tick only if this account has the same password on every host.");
            caution.TextWrapping = TextWrapping.Wrap;
            caution.FontStyle = Windows.UI.Text.FontStyle.Italic;
            lines.Children.Add(caution);
        }

        var box = new CheckBox { IsChecked = !_declined.Contains(key), Content = lines, HorizontalAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"Share one login for {suggestion.Login.Username}");
        box.Checked += (_, _) => { _declined.Remove(key); SelectionChanged(); };
        box.Unchecked += (_, _) => { _declined.Add(key); SelectionChanged(); };
        return box;
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SessionTreeMutedForegroundBrush"],
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private string LoginText(ImportCandidateVm candidate)
    {
        if (!candidate.CanImport)
            return "Already exists";
        if (!candidate.Candidate.IsSsh || !candidate.IsSelected)
            return "";
        var choice = _plan.Choices.GetValueOrDefault(candidate.Candidate);
        var name = choice?.Login is { } login
            ? _plan.Suggestions.FirstOrDefault(s => s.Login.Key == login.Key)?.Name
            : null;
        return choice?.Kind switch
        {
            ImportLoginKind.Inherit when name is not null => $"{name} · from folder",
            ImportLoginKind.Inherit => "Asked once per folder",
            ImportLoginKind.Identity => name ?? "",
            _ => candidate.Candidate.Username.Length > 0 ? "Own login" : "",
        };
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = _candidates.Where(c => c.IsSelected).Select(c => c.Candidate).ToList();
        if (confirmed.Count == 0)
            return;
        try
        {
            var before = App.Identities.Identities.Select(i => i.Id).ToHashSet();
            var (imported, duplicates) = SecureCrtImporter.Commit(App.Store, confirmed, App.SshKeys, _plan, App.Identities);
            var created = App.Identities.Identities.Where(i => !before.Contains(i.Id)).ToList();
            ShowResult(imported, duplicates, created);
            _completion.TrySetResult(new ImportOutcome(imported, duplicates, created.Select(i => i.Name).ToList()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            FooterText.Text = $"Import failed: {exception.Message}";
        }
    }

    private void ShowResult(int imported, int duplicates, IReadOnlyList<Resesh.Core.Models.Identity> created)
    {
        ReviewBody.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Collapsed;
        ResultBody.Visibility = Visibility.Visible;
        ResultTitle.Text = imported == 1 ? "Imported 1 session" : $"Imported {imported} sessions";
        var text = duplicates > 0 ? $"Skipped {Count(duplicates, "duplicate")}. " : "";
        text += created.Count == 0
            ? "Every session keeps its own login."
            : created.Count == 1
                ? "Created one identity. It asks for its password the first time you connect, then every session using it shares it."
                : $"Created {created.Count} identities. Each asks for its password the first time you connect, then every session using it shares it.";
        ResultText.Text = text;
        ManageIdentitiesButton.Visibility = created.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var resolver = App.Logins;
        var sessions = App.Store.Sessions;
        foreach (var identity in created)
        {
            var users = resolver.UsersOf(identity.Id, sessions).Count;
            var folders = App.Store.FolderSettings.Where(f => f.IdentityId == identity.Id).Select(f => f.Path).ToList();
            var card = new Border
            {
                Style = (Style)Resources["PanelCard"],
                Padding = new Thickness(16, 10, 16, 10),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = identity.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                        Muted($"{identity.Username} · {Count(users, "session")}"
                            + (folders.Count > 0 ? $" · {string.Join(", ", folders.Take(6))}{(folders.Count > 6 ? "…" : "")}" : "")),
                    },
                },
            };
            ResultIdentities.Children.Add(card);
        }
    }

    private void ManageIdentities_Click(object sender, RoutedEventArgs e) => ManageIdentitiesRequested?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    /// <summary>Two columns when there is room; sessions above logins when narrow.</summary>
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 980;
        LoginsColumn.Width = wide ? new GridLength(Math.Clamp(e.NewSize.Width * 0.42, 420, 760)) : new GridLength(0);
        Grid.SetColumn(LoginsPane, wide ? 1 : 0);
        Grid.SetRow(LoginsPane, wide ? 0 : 1);
        LoginsRow.Height = wide ? new GridLength(0) : GridLength.Auto;
        LoginsPane.MaxHeight = wide ? double.PositiveInfinity : Math.Max(160, e.NewSize.Height * 0.35);
    }
}
