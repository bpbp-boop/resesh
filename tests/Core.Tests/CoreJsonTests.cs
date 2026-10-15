using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Resesh.Core.Backup;
using Resesh.Core.History;
using Resesh.Core.Models;
using Resesh.Core.Ssh;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

/// <summary>
/// Pins the compile-time JSON metadata (CoreJsonContext) to the reflection-based serializer
/// the stores used before it, so existing files keep reading and writing the same way.
/// </summary>
public sealed class CoreJsonTests
{
    public static TheoryData<string, Type> Stores() => new()
    {
        { "SessionStore.JsonOptions", typeof(SessionStore.StoreData) },
        { "SshKeyStore.JsonOptions", typeof(SshKeyStore.StoreData) },
        { "IdentityStore.JsonOptions", typeof(IdentityStore.StoreData) },
        { "HighlightsStore.JsonOptions", typeof(HighlightsStore.StoreData) },
        { "WorkspaceStore.JsonOptions", typeof(WorkspaceStoreData) },
        { "SettingsStore.JsonOptions", typeof(AppSettings) },
        { "KnownHostsStore.JsonOptions", typeof(Dictionary<string, KnownHostEntry>) },
        { "CommandHistoryStore.LegacyJsonOptions", typeof(CommandHistoryEntry) },
        { "SessionsBackup.JsonOptions", typeof(BackupManifest) },
        { "SessionsBackup.JsonOptions", typeof(SessionsBackup.BackupSessionData) },
        { "SessionsBackup.JsonOptions", typeof(HighlightBackupData) },
        { "SessionsBackup.JsonOptions", typeof(List<SshKeyReference>) },
        { "SessionsBackup.JsonOptions", typeof(List<Identity>) },
        { "SessionsBackup.JsonOptions", typeof(IReadOnlyList<Identity>) },
        { "SessionsBackup.JsonOptions", typeof(Dictionary<Guid, string>) },
        { "SessionsBackup.JsonOptions", typeof(AppSettings) },
        { "SessionsBackup.JsonOptions", typeof(IReadOnlyDictionary<string, KnownHostEntry>) },
    };

    [Theory]
    [MemberData(nameof(Stores))]
    public void GeneratedMetadata_WritesWhatReflectionWrote(string options, Type root)
    {
        var (generated, reflection) = OptionsPair(options);
        var value = Sample.Create(root);

        var expected = JsonSerializer.Serialize(value, root, reflection);
        Assert.Equal(expected, JsonSerializer.Serialize(value, generated.GetTypeInfo(root)));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void GeneratedMetadata_ReadsWhatReflectionWrote(string options, Type root)
    {
        var (generated, reflection) = OptionsPair(options);
        var json = JsonSerializer.Serialize(Sample.Create(root), root, reflection);

        var read = JsonSerializer.Deserialize(json, generated.GetTypeInfo(root));
        Assert.Equal(json, JsonSerializer.Serialize(read, root, reflection));
    }

    /// <summary>Nulls and defaults exercise the omit-when-null and omit-when-default rules.</summary>
    [Theory]
    [MemberData(nameof(PersistedObjectTypes))]
    public void GeneratedMetadata_WritesDefaultsLikeReflection(Type type)
    {
        var (generated, reflection) = OptionsPair("SessionsBackup.JsonOptions");
        var value = Activator.CreateInstance(type);

        Assert.Equal(JsonSerializer.Serialize(value, type, reflection), JsonSerializer.Serialize(value, generated.GetTypeInfo(type)));
    }

    /// <summary>Files from older versions lack newer values; each must keep its initializer.</summary>
    [Theory]
    [MemberData(nameof(PersistedObjectTypes))]
    public void GeneratedMetadata_KeepsInitializersForMissingValues(Type type)
    {
        var (generated, reflection) = OptionsPair("SessionsBackup.JsonOptions");
        JsonObject Fresh() => JsonSerializer.SerializeToNode(Activator.CreateInstance(type), type, reflection)!.AsObject();
        var expected = Fresh();
        // Initializers such as Id = Guid.NewGuid() differ between any two new instances.
        var other = Fresh();
        foreach (var name in expected.Select(pair => pair.Key).ToList())
        {
            if (!JsonNode.DeepEquals(expected[name], other[name]))
                expected.Remove(name);
        }

        var read = JsonSerializer.SerializeToNode(JsonSerializer.Deserialize("{}", generated.GetTypeInfo(type)), type, reflection)!.AsObject();
        foreach (var name in read.Select(pair => pair.Key).Where(name => !expected.ContainsKey(name)).ToList())
            read.Remove(name);
        Assert.Equal(expected.ToJsonString(), read.ToJsonString());
    }

    [Fact]
    public void CamelCaseEnumConverter_CoversEveryEnumItsStoresWrite()
    {
        var roots = Stores().Where(row => OptionsPair((string)row[0]).Generated.Converters.Contains(CamelCaseEnumConverter.Instance))
            .Select(row => (Type)row[1]);
        var enums = Reachable(roots).Select(type => Nullable.GetUnderlyingType(type) ?? type).Where(type => type.IsEnum).ToHashSet();
        Assert.Empty(enums.Except(CamelCaseEnumConverter.EnumTypes).Select(type => type.Name));
    }

    public static TheoryData<Type> PersistedObjectTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in Reachable(Stores().Select(row => (Type)row[1])).Where(Sample.IsObject).Where(type => type.GetConstructor(Type.EmptyTypes) is not null))
            data.Add(type);
        return data;
    }

    private static (JsonSerializerOptions Generated, JsonSerializerOptions Reflection) OptionsPair(string name)
    {
        var parts = name.Split('.');
        var owner = typeof(SessionStore).Assembly.GetTypes().Single(type => type.Name == parts[0]);
        var generated = (JsonSerializerOptions)owner
            .GetField(parts[1], BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        // What the store used before: reflection metadata and the reflection enum converter.
        var reflection = new JsonSerializerOptions(generated) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        if (reflection.Converters.Remove(CamelCaseEnumConverter.Instance))
            reflection.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return (generated, reflection);
    }

    /// <summary>Every type the roots can contain: properties, elements, keys.</summary>
    private static HashSet<Type> Reachable(IEnumerable<Type> roots)
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>(roots);
        while (pending.TryPop(out var type))
        {
            if (!seen.Add(type))
                continue;
            foreach (var argument in type.GetGenericArguments())
                pending.Push(argument);
            if (type.IsArray)
                pending.Push(type.GetElementType()!);
            if (Sample.IsObject(type))
                foreach (var property in Sample.Properties(type))
                    pending.Push(property.PropertyType);
        }
        return seen;
    }

    /// <summary>Builds a value with every member set to a non-default value.</summary>
    private static class Sample
    {
        public static bool IsObject(Type type) =>
            type.IsClass && type != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(type) && !type.IsAbstract;

        public static IEnumerable<PropertyInfo> Properties(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0
                    && property.GetCustomAttribute<JsonIgnoreAttribute>() is null
                    && property.GetMethod is { IsPublic: true });

        public static object? Create(Type type, int depth = 0)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying is not null)
                return Create(underlying, depth);
            if (type == typeof(string)) return "sample \"text\" <é>";
            if (type == typeof(bool)) return true;
            if (type == typeof(int)) return 7;
            if (type == typeof(long)) return 7_000_000_000L;
            if (type == typeof(uint)) return 7u;
            if (type == typeof(double)) return 1.5;
            if (type == typeof(Guid)) return new Guid("6cbde03c-e3cf-3bd6-b0c0-84dc4c84d9a4");
            if (type == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 10, 15, 8, 30, 0, TimeSpan.FromHours(11));
            if (type == typeof(DateTime)) return new DateTime(2026, 10, 15, 8, 30, 0, DateTimeKind.Utc);
            if (type == typeof(TimeSpan)) return TimeSpan.FromSeconds(90);
            if (type.IsEnum) return Enum.GetValues(type).Cast<object>().Last();
            if (depth > 6) return null;
            if (type.IsArray)
            {
                var array = Array.CreateInstance(type.GetElementType()!, 1);
                array.SetValue(Create(type.GetElementType()!, depth + 1), 0);
                return array;
            }
            if (type.IsGenericType && Dictionaries.Contains(type.GetGenericTypeDefinition()))
            {
                var args = type.GetGenericArguments();
                var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args))!;
                dictionary.Add(Create(args[0], depth + 1)!, Create(args[1], depth + 1));
                return dictionary;
            }
            if (type.IsGenericType && Lists.Contains(type.GetGenericTypeDefinition()))
            {
                var element = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
                list.Add(Create(element, depth + 1));
                return type.GetGenericTypeDefinition() == typeof(HashSet<>)
                    ? Activator.CreateInstance(type, list)
                    : list;
            }
            if (!IsObject(type))
                throw new NotSupportedException($"No sample for {type}.");

            var constructor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
            var value = constructor.Invoke(constructor.GetParameters().Select(p => Create(p.ParameterType, depth + 1)).ToArray());
            foreach (var property in Properties(type).Where(property => property.SetMethod is not null))
                property.SetValue(value, Create(property.PropertyType, depth + 1));
            return value;
        }

        private static readonly HashSet<Type> Dictionaries =
            [typeof(Dictionary<,>), typeof(IDictionary<,>), typeof(IReadOnlyDictionary<,>)];

        private static readonly HashSet<Type> Lists =
            [typeof(List<>), typeof(IList<>), typeof(IReadOnlyList<>), typeof(IEnumerable<>), typeof(ICollection<>),
             typeof(IReadOnlyCollection<>), typeof(HashSet<>)];
    }
}
