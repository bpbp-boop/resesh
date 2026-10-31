using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Resesh.Core.Backup;
using Resesh.Core.History;
using Resesh.Core.Models;
using Resesh.Core.Ssh;

namespace Resesh.Core.Storage;

/// <summary>
/// Compile-time JSON metadata for everything Core reads and writes, so serialization needs
/// no reflection: required for ahead-of-time compilation, and cheaper at launch. Each store
/// keeps its own options (naming, indentation, enums); <see cref="CoreJson.With"/> points
/// them at this metadata.
/// </summary>
/// <remarks>
/// Generated metadata sets init-only properties through the constructor, so a value missing
/// from the file would replace the property's initializer with default(T). Persisted types
/// therefore use settable properties wherever an initializer matters; CoreJsonTests checks
/// every type registered here.
/// </remarks>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SessionStore.StoreData), TypeInfoPropertyName = "SessionStoreData")]
[JsonSerializable(typeof(SshKeyStore.StoreData), TypeInfoPropertyName = "SshKeyStoreData")]
[JsonSerializable(typeof(IdentityStore.StoreData), TypeInfoPropertyName = "IdentityStoreData")]
[JsonSerializable(typeof(HighlightsStore.StoreData), TypeInfoPropertyName = "HighlightsStoreData")]
[JsonSerializable(typeof(WorkspaceStoreData))]
[JsonSerializable(typeof(Dictionary<string, KnownHostEntry>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, KnownHostEntry>))]
[JsonSerializable(typeof(CommandHistoryEntry))]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(SessionsBackup.BackupSessionData))]
[JsonSerializable(typeof(HighlightBackupData))]
[JsonSerializable(typeof(List<SshKeyReference>))]
[JsonSerializable(typeof(List<Identity>))]
[JsonSerializable(typeof(IReadOnlyList<Identity>))]
[JsonSerializable(typeof(Dictionary<Guid, string>))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;

internal static class CoreJson
{
    /// <summary>Returns <paramref name="options"/> resolving metadata from <see cref="CoreJsonContext"/>.</summary>
    public static JsonSerializerOptions With(JsonSerializerOptions options)
    {
        options.TypeInfoResolver = CoreJsonContext.Default;
        options.MakeReadOnly();
        return options;
    }

    /// <summary>The metadata for <typeparamref name="T"/> under <paramref name="options"/>.</summary>
    public static JsonTypeInfo<T> Info<T>(this JsonSerializerOptions options) =>
        (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}

/// <summary>
/// Enums as camelCase strings, as JsonStringEnumConverter(JsonNamingPolicy.CamelCase) wrote
/// them. That converter builds its per-enum converter by reflection, which ahead-of-time
/// compilation cannot do, so the enums Core persists are listed here.
/// </summary>
internal sealed class CamelCaseEnumConverter : JsonConverterFactory
{
    public static readonly CamelCaseEnumConverter Instance = new();

    private static readonly Dictionary<Type, JsonConverterFactory> Converters = new()
    {
        [typeof(SessionKind)] = Create<SessionKind>(),
        [typeof(AuthMethod)] = Create<AuthMethod>(),
        [typeof(IdentityProof)] = Create<IdentityProof>(),
        [typeof(ShellIntegrationMode)] = Create<ShellIntegrationMode>(),
        [typeof(DetachedSessionAction)] = Create<DetachedSessionAction>(),
        [typeof(TelnetTextEncoding)] = Create<TelnetTextEncoding>(),
        [typeof(TunnelKind)] = Create<TunnelKind>(),
        [typeof(ProxyKind)] = Create<ProxyKind>(),
        [typeof(SerialParity)] = Create<SerialParity>(),
        [typeof(SerialStopBits)] = Create<SerialStopBits>(),
        [typeof(SerialFlowControl)] = Create<SerialFlowControl>(),
    };

    /// <summary>The enums this converter handles (tests check it covers every persisted enum).</summary>
    public static IEnumerable<Type> EnumTypes => Converters.Keys;

    public override bool CanConvert(Type typeToConvert) => Converters.ContainsKey(typeToConvert);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        Converters[typeToConvert].CreateConverter(typeToConvert, options);

    private static JsonConverterFactory Create<T>() where T : struct, Enum =>
        new JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase);
}
