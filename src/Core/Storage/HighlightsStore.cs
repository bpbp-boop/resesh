using System.Text.Json;
using Sessions.Core.Models;

namespace Sessions.Core.Storage;

/// <summary>
/// Highlight-rule state in highlights.json: global enable/disable deltas and definition
/// overrides for built-in rules, plus full user-defined custom rules. Built-in rule
/// definitions live in code (<see cref="BuiltinHighlights"/>) so app updates can fix or
/// extend packs; only the user's deviations from the defaults are persisted. Writes are
/// atomic with .bak rotation, mirroring SessionStore.
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
    private Dictionary<string, HighlightRule> _overrides = new(StringComparer.Ordinal);

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
            // Same stance for overrides of built-in ids this build doesn't know (e.g. from
            // a newer app's file): kept and re-saved, just never applied.
            _overrides = (data.BuiltinOverrides ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r.Id))
                .GroupBy(r => r.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        }
    }

    /// <summary>All rules — built-ins with the user's definition overrides and the global
    /// enable deltas applied, then custom rules — in application order (later rules win on
    /// overlapping matches).</summary>
    public IReadOnlyList<HighlightRule> AllRules
    {
        get
        {
            lock (_gate)
            {
                return BuiltinHighlights.Rules
                    .Select(r => Merged(r) with { Enabled = EffectiveGlobal(r) })
                    .Concat(_custom.Select(r => r with { Pack = "custom" }))
                    .ToList();
            }
        }
    }

    /// <summary>A stable copy of the persisted highlight deltas and custom rules.</summary>
    public HighlightBackupData ExportBackup()
    {
        lock (_gate)
        {
            return new HighlightBackupData
            {
                EnabledRules = _enabled.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                DisabledRules = _disabled.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                CustomRules = _custom.ToList(),
                BuiltinOverrides = _overrides.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
            };
        }
    }

    /// <summary>
    /// Merges imported state. Imported deltas and same-id custom rules take precedence;
    /// unrelated local rules remain.
    /// </summary>
    public void MergeBackup(HighlightBackupData imported)
    {
        lock (_gate)
        {
            foreach (var id in imported.EnabledRules)
            {
                _disabled.Remove(id);
                _enabled.Add(id);
            }
            foreach (var id in imported.DisabledRules)
            {
                _enabled.Remove(id);
                _disabled.Add(id);
            }
            foreach (var rule in imported.CustomRules.Where(r => !string.IsNullOrWhiteSpace(r.Id)))
            {
                var normalized = rule with { Pack = "custom" };
                var index = _custom.FindIndex(r => r.Id == normalized.Id);
                if (index >= 0)
                    _custom[index] = normalized;
                else
                    _custom.Add(normalized);
            }
            foreach (var rule in imported.BuiltinOverrides.Where(r => !string.IsNullOrWhiteSpace(r.Id)))
            {
                // Known builtin: normalize (and drop a default-identical override); unknown
                // id: keep raw so a newer app's data survives the round trip.
                if (BuiltinHighlights.Rules.FirstOrDefault(r => r.Id == rule.Id) is { } builtin)
                {
                    var normalized = rule with { Id = builtin.Id, Pack = builtin.Pack, Enabled = builtin.Enabled };
                    if (normalized == builtin)
                        _overrides.Remove(builtin.Id);
                    else
                        _overrides[builtin.Id] = normalized;
                }
                else
                {
                    _overrides[rule.Id] = rule;
                }
            }
            Save();
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

    /// <summary>Replaces a built-in rule's definition (name, pattern, style). Enabled state
    /// stays with the delta system (<see cref="SetEnabled"/>) — the override's Enabled field
    /// is ignored. An override matching the shipped definition is removed again rather than
    /// stored, same as an enabled delta that returns to the default.</summary>
    public void SaveBuiltinOverride(HighlightRule rule)
    {
        lock (_gate)
        {
            if (BuiltinHighlights.Rules.FirstOrDefault(r => r.Id == rule.Id) is not { } builtin)
                throw new ArgumentException($"No built-in rule '{rule.Id}'.", nameof(rule));
            var normalized = rule with { Id = builtin.Id, Pack = builtin.Pack, Enabled = builtin.Enabled };
            if (normalized == builtin)
                _overrides.Remove(builtin.Id);
            else
                _overrides[builtin.Id] = normalized;
            Save();
        }
    }

    /// <summary>Drops a built-in rule's definition override, restoring the shipped
    /// definition. The enabled state is untouched — that's the checkbox's job.</summary>
    public bool ResetBuiltin(string id)
    {
        lock (_gate)
        {
            var removed = _overrides.Remove(id);
            if (removed)
                Save();
            return removed;
        }
    }

    /// <summary>Whether a built-in rule's definition deviates from the shipped default.</summary>
    public bool IsOverridden(string id)
    {
        lock (_gate)
        {
            return _overrides.ContainsKey(id);
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

    /// <summary>The built-in rule with the user's definition override applied, if any.
    /// Id and pack always come from the shipped rule so an override can't detach a rule
    /// from its identity.</summary>
    private HighlightRule Merged(HighlightRule builtin) =>
        _overrides.TryGetValue(builtin.Id, out var over)
            ? over with { Id = builtin.Id, Pack = builtin.Pack }
            : builtin;

    private void Save()
    {
        var data = new StoreData
        {
            EnabledRules = _enabled.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            DisabledRules = _disabled.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            CustomRules = _custom,
            BuiltinOverrides = _overrides.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
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
        public List<HighlightRule>? BuiltinOverrides { get; set; }
    }
}

public sealed record HighlightBackupData
{
    public IReadOnlyList<string> EnabledRules { get; init; } = [];
    public IReadOnlyList<string> DisabledRules { get; init; } = [];
    public IReadOnlyList<HighlightRule> CustomRules { get; init; } = [];
    public IReadOnlyList<HighlightRule> BuiltinOverrides { get; init; } = [];
}
