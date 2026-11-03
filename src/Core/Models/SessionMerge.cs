namespace Resesh.Core.Models;

/// <summary>
/// Saves an editor's changes without undoing what changed elsewhere while it was open.
/// An editor opens on one version of a session; meanwhile another window can move its
/// folder, the first connect can suggest an icon, or the tab's Highlighting menu can toggle
/// a rule. <see cref="Rebase"/> keeps every field the editor left as it found it at the
/// latest saved value, and takes the editor's value only where the user changed it.
/// </summary>
public static class SessionMerge
{
    /// <summary>The latest saved session with the changes from <paramref name="original"/> to
    /// <paramref name="edited"/> applied. Fields that only make sense together (the login, the
    /// route, persistence) move as one, so a merge never pairs halves of two different logins.</summary>
    /// <remarks>Lists every property: SessionMergeTests fails when one is added and not listed.</remarks>
    public static Session Rebase(Session original, Session edited, Session latest)
    {
        T Pick<T>(Func<Session, T> field, Func<T, T, bool>? same = null)
        {
            same ??= EqualityComparer<T>.Default.Equals;
            return same(field(edited), field(original)) ? field(latest) : field(edited);
        }

        var loginEdited = !SameLogin(edited, original);
        var login = loginEdited ? edited : latest;
        var routeEdited = edited.JumpHostId != original.JumpHostId || edited.Proxy != original.Proxy;
        var route = routeEdited ? edited : latest;
        var persistenceEdited = edited.Persistent != original.Persistent || edited.SessionAgent != original.SessionAgent;
        var persistence = persistenceEdited ? edited : latest;

        return latest with
        {
            Name = Pick(s => s.Name),
            Kind = Pick(s => s.Kind),
            Local = Pick(s => s.Local, SameLocal),
            BuiltIn = Pick(s => s.BuiltIn),
            Serial = Pick(s => s.Serial),
            FolderPath = Pick(s => s.FolderPath),
            Host = Pick(s => s.Host),
            Port = Pick(s => s.Port),
            Username = login.Username,
            IdentityId = login.IdentityId,
            InheritLogin = login.InheritLogin,
            AuthMethod = login.AuthMethod,
            PrivateKeyId = login.PrivateKeyId,
            PrivateKeyPath = login.PrivateKeyPath,
            PassphraseRequired = login.PassphraseRequired,
            JumpHostId = route.JumpHostId,
            Proxy = route.Proxy,
            Tunnels = Pick(s => s.Tunnels, SameList),
            TerminalType = Pick(s => s.TerminalType),
            TelnetEncoding = Pick(s => s.TelnetEncoding),
            TelnetReport80x25 = Pick(s => s.TelnetReport80x25),
            TelnetFixed80Columns = Pick(s => s.TelnetFixed80Columns),
            Algorithms = Pick(s => s.Algorithms, SameAlgorithms),
            PasteLineDelay = Pick(s => s.PasteLineDelay),
            PasteCharacterDelay = Pick(s => s.PasteCharacterDelay),
            Persistent = persistence.Persistent,
            SessionAgent = persistence.SessionAgent,
            DetachedSessions = Pick(s => s.DetachedSessions),
            ShellIntegration = Pick(s => s.ShellIntegration),
            Notes = Pick(s => s.Notes),
            ColorTag = Pick(s => s.ColorTag),
            Icon = Pick(s => s.Icon),
            Agent = Pick(s => s.Agent),
            Overrides = RebaseOverrides(original.Overrides, edited.Overrides, latest.Overrides),
        };
    }

    /// <summary>The overrides one field at a time: a theme picked in the editor and a highlight
    /// rule toggled from the tab meanwhile both survive. All-null overrides are stored as null.</summary>
    private static TerminalOverrides? RebaseOverrides(
        TerminalOverrides? original, TerminalOverrides? edited, TerminalOverrides? latest)
    {
        TerminalOverrides empty = new();
        var before = original ?? empty;
        var after = edited ?? empty;
        var now = latest ?? empty;

        T Pick<T>(Func<TerminalOverrides, T> field, Func<T, T, bool>? same = null)
        {
            same ??= EqualityComparer<T>.Default.Equals;
            return same(field(after), field(before)) ? field(now) : field(after);
        }

        var merged = now with
        {
            Theme = Pick(o => o.Theme),
            FontFamily = Pick(o => o.FontFamily),
            FontSize = Pick(o => o.FontSize),
            Scrollback = Pick(o => o.Scrollback),
            MouseCapture = Pick(o => o.MouseCapture),
            AlwaysRecord = Pick(o => o.AlwaysRecord),
            KeepCommandHistory = Pick(o => o.KeepCommandHistory),
            EnabledRules = Pick(o => o.EnabledRules, SameList),
            DisabledRules = Pick(o => o.DisabledRules, SameList),
        };
        return merged.IsEmpty ? null : merged;
    }

    private static bool SameLogin(Session first, Session second) =>
        first.Username == second.Username
        && first.IdentityId == second.IdentityId
        && first.InheritLogin == second.InheritLogin
        && first.AuthMethod == second.AuthMethod
        && first.PrivateKeyId == second.PrivateKeyId
        && first.PrivateKeyPath == second.PrivateKeyPath
        && first.PassphraseRequired == second.PassphraseRequired;

    // Records compare their list members by reference; editors build new lists on every save.
    private static bool SameList<T>(IReadOnlyList<T>? first, IReadOnlyList<T>? second) =>
        first is null || second is null ? first is null && second is null : first.SequenceEqual(second);

    private static bool SameAlgorithms(SshAlgorithms? first, SshAlgorithms? second) =>
        first is null || second is null
            ? first is null && second is null
            : SameList(first.DisabledKeyExchange, second.DisabledKeyExchange)
                && SameList(first.DisabledCiphers, second.DisabledCiphers)
                && SameList(first.DisabledMacs, second.DisabledMacs)
                && SameList(first.DisabledHostKeys, second.DisabledHostKeys);

    private static bool SameLocal(LocalTarget? first, LocalTarget? second) =>
        first is null || second is null
            ? first is null && second is null
            : first.Executable == second.Executable
                && first.StartingDirectory == second.StartingDirectory
                && first.Arguments.SequenceEqual(second.Arguments)
                && (first.Environment ?? EmptyEnvironment).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .SequenceEqual((second.Environment ?? EmptyEnvironment).OrderBy(pair => pair.Key, StringComparer.Ordinal));

    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment = new Dictionary<string, string>();
}
