using System.Text.Json;
using Sessions.Core.Models;

namespace Sessions.Core.Storage;

/// <summary>
/// Highlight-rule state in highlights.json: global enable/disable deltas for built-in
/// rules plus full user-defined custom rules. Built-in rule definitions live in code
/// (<see cref="BuiltinHighlights"/>) so app updates can fix or extend packs; only the
/// user's deviations from the defaults are persisted. Writes are atomic with .bak
/// rotation, mirroring SessionStore.
/// </summary>
public sealed class HighlightsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly string _bakPath;
    private readonly object _gate = new();

    private HashSet<string> _enabled = new(StringComparer.Ordinal);
    private HashSet<string> _disabled = new(StringComparer.Ordinal);
    private List<HighlightRule> _custom = [];

    public HighlightsStore(string path)
    {
        _path = path;
        _bakPath = path + ".bak";
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sessions", "highlights.json");

    public void Load()
    {
        lock (_gate)
        {
            var data = TryRead(_path) ?? TryRead(_bakPath) ?? new StoreData();
            _enabled = new HashSet<string>(data.EnabledRules ?? [], StringComparer.Ordinal);
            _disabled = new HashSet<string>(data.DisabledRules ?? [], StringComparer.Ordinal);
            // A custom rule with an invalid regex is kept on disk but never offered/applied;
            // dropping it silently would delete user data over a typo we let through.
            _custom = (data.CustomRules ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Id)).ToList();
        }
    }

    /// <summary>All rules — built-ins with the global deltas applied, then custom rules —
    /// in application order (later rules win on overlapping matches).</summary>
    public IReadOnlyList<HighlightRule> AllRules
    {
        get
        {
            lock (_gate)
            {
                return BuiltinHighlights.Rules
                    .Select(r => r with { Enabled = EffectiveGlobal(r) })
                    .Concat(_custom.Select(r => r with { Pack = "custom" }))
                    .ToList();
            }
        }
    }

    /// <summary>Sets a rule's global enabled state. For built-ins this stores a delta
    /// (removed again when it matches the shipped default); custom rules are rewritten.</summary>
    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var custom = _custom.FindIndex(r => r.Id == id);
            if (custom >= 0)
            {
                _custom[custom] = _custom[custom] with { Enabled = enabled };
            }
            else if (BuiltinHighlights.Rules.FirstOrDefault(r => r.Id == id) is { } builtin)
            {
                _enabled.Remove(id);
                _disabled.Remove(id);
                if (enabled != builtin.Enabled)
                    (enabled ? _enabled : _disabled).Add(id);
            }
            else
            {
                return;
            }
            Save();
        }
    }

    /// <summary>Adds or replaces (by id) a user-defined rule. The pattern must already be
    /// validated by the caller; the page additionally guards against regexes that fail
    /// to compile in JavaScript.</summary>
    public void SaveCustom(HighlightRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Id))
            throw new ArgumentException("Custom rule needs an id.", nameof(rule));
        lock (_gate)
        {
            rule = rule with { Pack = "custom" };
            var index = _custom.FindIndex(r => r.Id == rule.Id);
            if (index >= 0)
                _custom[index] = rule;
            else
                _custom.Add(rule);
            Save();
        }
    }

    public bool RemoveCustom(string id)
    {
        lock (_gate)
        {
            var removed = _custom.RemoveAll(r => r.Id == id) > 0;
            if (removed)
                Save();
            return removed;
        }
    }

    /// <summary>The enabled rules for a session: global state with the session's
    /// enable/disable deltas layered on top. This is what gets sent to the page.</summary>
    public IReadOnlyList<HighlightRule> ResolveForSession(TerminalOverrides? overrides)
    {
        var enabledDelta = overrides?.EnabledRules ?? [];
        var disabledDelta = overrides?.DisabledRules ?? [];
        return AllRules
            .Where(r => disabledDelta.Contains(r.Id) ? false
                : enabledDelta.Contains(r.Id) || r.Enabled)
            .ToList();
    }

    private bool EffectiveGlobal(HighlightRule builtin) =>
        !_disabled.Contains(builtin.Id) && (builtin.Enabled || _enabled.Contains(builtin.Id));

    private void Save()
    {
        var data = new StoreData
        {
            EnabledRules = _enabled.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            DisabledRules = _disabled.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            CustomRules = _custom,
        };
        var json = JsonSerializer.Serialize(data, JsonOptions);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmpPath = _path + ".tmp";
        File.WriteAllText(tmpPath, json);

        if (File.Exists(_path))
            File.Replace(tmpPath, _path, _bakPath);
        else
            File.Move(tmpPath, _path);
    }

    private static StoreData? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<StoreData>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    private sealed class StoreData
    {
        public List<string>? EnabledRules { get; set; }
        public List<string>? DisabledRules { get; set; }
        public List<HighlightRule>? CustomRules { get; set; }
    }
}
