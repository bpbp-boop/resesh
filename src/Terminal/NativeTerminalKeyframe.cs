using System.Buffers.Binary;
using System.Text;

namespace Resesh.Terminal;

internal sealed record NativeTerminalSnapshotEnvelope(
    string BuildId,
    byte[] NativeSnapshot,
    byte[] PendingUtf8,
    string SearchQuery,
    bool SearchCaseSensitive,
    bool SearchRegularExpression,
    IReadOnlyList<NativeTerminalApi.HighlightRulePayload> HighlightRules);

/// <summary>
/// Portable playback envelope around the native TerminalCore snapshot.
///
/// Header (24 bytes, little-endian):
/// magic "RSNP", schema major/minor, feature flags, field payload length,
/// CRC-32 of the field payload, and a reserved word.
/// Each field is a uint16 id, uint16 flags, uint32 byte length, then its bytes.
/// Unknown fields are skipped. All sizes are validated before allocation.
/// </summary>
internal static class NativeTerminalSnapshotCodec
{
    private const uint Magic = 0x504E5352;
    private const ushort SchemaMajor = 1;
    private const ushort SchemaMinor = 0;
    private const int HeaderLength = 24;
    private const int FieldHeaderLength = 8;
    private const int MaximumEnvelopeLength = 32 * 1024 * 1024;
    private const int MaximumBuildIdLength = 256;
    private const int MaximumSearchLength = 1024 * 1024;
    private const int MaximumHighlightRules = 1024;
    private const int MaximumPatternLength = 1024 * 1024;

    private const ushort BuildIdField = 1;
    private const ushort NativeSnapshotField = 2;
    private const ushort PendingUtf8Field = 3;
    private const ushort SearchField = 4;
    private const ushort HighlightRulesField = 5;

    private const uint PendingUtf8Feature = 0x00000001;
    private const uint SearchFeature = 0x00000002;
    private const uint HighlightRulesFeature = 0x00000004;

    internal static byte[] Encode(
        ReadOnlySpan<byte> nativeSnapshot,
        string buildId,
        ReadOnlySpan<byte> pendingUtf8,
        string searchQuery,
        bool searchCaseSensitive,
        bool searchRegularExpression,
        IReadOnlyList<NativeTerminalApi.HighlightRulePayload> highlightRules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        ArgumentNullException.ThrowIfNull(searchQuery);
        ArgumentNullException.ThrowIfNull(highlightRules);
        if (nativeSnapshot.IsEmpty || nativeSnapshot.Length > MaximumEnvelopeLength)
            throw new InvalidDataException("The native snapshot length is invalid.");
        if (!SnapshotUtf8Decoder.IsValidPending(pendingUtf8))
            throw new InvalidDataException("The pending UTF-8 decoder state is invalid.");
        if (highlightRules.Count > MaximumHighlightRules)
            throw new InvalidDataException("The highlight rule count is invalid.");

        var buildIdBytes = Encoding.UTF8.GetBytes(buildId);
        var searchBytes = Encoding.UTF8.GetBytes(searchQuery);
        if (buildIdBytes.Length is 0 or > MaximumBuildIdLength)
            throw new InvalidDataException("The terminal build ID length is invalid.");
        if (searchBytes.Length > MaximumSearchLength)
            throw new InvalidDataException("The terminal search query is too long.");

        using var fields = new MemoryStream();
        using (var writer = new BinaryWriter(fields, Encoding.UTF8, leaveOpen: true))
        {
            WriteField(writer, BuildIdField, buildIdBytes);
            WriteField(writer, NativeSnapshotField, nativeSnapshot);
            if (!pendingUtf8.IsEmpty)
                WriteField(writer, PendingUtf8Field, pendingUtf8);

            using var search = new MemoryStream();
            using (var searchWriter = new BinaryWriter(search, Encoding.UTF8, leaveOpen: true))
            {
                searchWriter.Write((byte)(searchCaseSensitive ? 1 : 0));
                searchWriter.Write((byte)(searchRegularExpression ? 1 : 0));
                searchWriter.Write((ushort)0);
                searchWriter.Write(checked((uint)searchBytes.Length));
                searchWriter.Write(searchBytes);
            }
            WriteField(writer, SearchField, search.GetBuffer().AsSpan(0, checked((int)search.Length)));

            using var rules = new MemoryStream();
            using (var rulesWriter = new BinaryWriter(rules, Encoding.UTF8, leaveOpen: true))
            {
                rulesWriter.Write(checked((uint)highlightRules.Count));
                foreach (var rule in highlightRules)
                {
                    var pattern = Encoding.UTF8.GetBytes(rule.Pattern);
                    if (pattern.Length > MaximumPatternLength)
                        throw new InvalidDataException("A highlight pattern is too long.");
                    uint ruleFlags = 0;
                    if (rule.RegularExpression)
                        ruleFlags |= 1;
                    if (rule.MatchCase)
                        ruleFlags |= 2;
                    if (rule.ShowInOverview)
                        ruleFlags |= 4;
                    rulesWriter.Write(rule.Id);
                    rulesWriter.Write(ruleFlags);
                    rulesWriter.Write(rule.Foreground);
                    rulesWriter.Write(rule.Background);
                    rulesWriter.Write(rule.Priority);
                    rulesWriter.Write(checked((uint)pattern.Length));
                    rulesWriter.Write(pattern);
                }
            }
            WriteField(writer, HighlightRulesField, rules.GetBuffer().AsSpan(0, checked((int)rules.Length)));
        }

        if (fields.Length > MaximumEnvelopeLength - HeaderLength)
            throw new InvalidDataException("The terminal snapshot envelope is too large.");
        var fieldBytes = fields.GetBuffer().AsSpan(0, checked((int)fields.Length));
        var result = new byte[HeaderLength + fieldBytes.Length];
        var header = result.AsSpan(0, HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], SchemaMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], SchemaMinor);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[8..],
            (pendingUtf8.IsEmpty ? 0u : PendingUtf8Feature) | SearchFeature | HighlightRulesFeature);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], checked((uint)fieldBytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32(fieldBytes));
        fieldBytes.CopyTo(result.AsSpan(HeaderLength));
        return result;
    }

    internal static NativeTerminalSnapshotEnvelope Decode(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < HeaderLength || envelope.Length > MaximumEnvelopeLength)
            throw new InvalidDataException("The terminal snapshot envelope length is invalid.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(envelope) != Magic)
            throw new InvalidDataException("The terminal snapshot envelope magic is invalid.");
        var major = BinaryPrimitives.ReadUInt16LittleEndian(envelope[4..]);
        if (major != SchemaMajor)
            throw new InvalidDataException($"Terminal snapshot schema major {major} is not supported.");
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(envelope[6..]);
        var featureFlags = BinaryPrimitives.ReadUInt32LittleEndian(envelope[8..]);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(envelope[12..]);
        if (payloadLength != envelope.Length - HeaderLength)
            throw new InvalidDataException("The terminal snapshot payload length is invalid.");
        var payload = envelope[HeaderLength..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(envelope[16..]) != Crc32(payload))
            throw new InvalidDataException("The terminal snapshot checksum is invalid.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(envelope[20..]) != 0)
            throw new InvalidDataException("The terminal snapshot reserved header is invalid.");

        string? buildId = null;
        byte[]? nativeSnapshot = null;
        byte[] pendingUtf8 = [];
        var searchQuery = string.Empty;
        var searchCaseSensitive = false;
        var searchRegularExpression = false;
        IReadOnlyList<NativeTerminalApi.HighlightRulePayload> highlightRules = [];
        var hasPendingUtf8 = false;
        var hasSearch = false;
        var hasHighlightRules = false;

        var offset = 0;
        while (offset < payload.Length)
        {
            if (payload.Length - offset < FieldHeaderLength)
                throw new InvalidDataException("The terminal snapshot field header is truncated.");
            var id = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
            var fieldFlags = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 2)..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 4)..]);
            offset += FieldHeaderLength;
            if (length > payload.Length - offset)
                throw new InvalidDataException("The terminal snapshot field is truncated.");
            var field = payload.Slice(offset, checked((int)length));
            offset += checked((int)length);

            switch (id)
            {
                case BuildIdField:
                    if (fieldFlags != 0)
                        throw new InvalidDataException("The terminal snapshot build ID field flags are invalid.");
                    if (field.IsEmpty || field.Length > MaximumBuildIdLength || buildId is not null)
                        throw new InvalidDataException("The terminal snapshot build ID field is invalid.");
                    buildId = DecodeUtf8(field, "build ID");
                    break;
                case NativeSnapshotField:
                    if (fieldFlags != 0)
                        throw new InvalidDataException("The native terminal snapshot field flags are invalid.");
                    if (field.IsEmpty || field.Length > MaximumEnvelopeLength || nativeSnapshot is not null)
                        throw new InvalidDataException("The native terminal snapshot field is invalid.");
                    nativeSnapshot = field.ToArray();
                    break;
                case PendingUtf8Field:
                    if (fieldFlags != 0 || field.Length is 0 or > 3 || hasPendingUtf8)
                        throw new InvalidDataException("The pending UTF-8 field is invalid.");
                    if (!SnapshotUtf8Decoder.IsValidPending(field))
                        throw new InvalidDataException("The pending UTF-8 decoder state is invalid.");
                    pendingUtf8 = field.ToArray();
                    hasPendingUtf8 = true;
                    break;
                case SearchField:
                    if (fieldFlags != 0 || hasSearch)
                        throw new InvalidDataException("The terminal search field is duplicated or flagged.");
                    DecodeSearch(field, out searchQuery, out searchCaseSensitive, out searchRegularExpression);
                    hasSearch = true;
                    break;
                case HighlightRulesField:
                    if (fieldFlags != 0 || hasHighlightRules)
                        throw new InvalidDataException("The terminal highlight field is duplicated or flagged.");
                    highlightRules = DecodeHighlightRules(field);
                    hasHighlightRules = true;
                    break;
                default:
                    // A newer minor schema may append length-delimited fields.
                    break;
            }
        }

        if (buildId is null || nativeSnapshot is null)
            throw new InvalidDataException("The terminal snapshot is missing required fields.");
        if (((featureFlags & PendingUtf8Feature) != 0) != hasPendingUtf8)
            throw new InvalidDataException("The terminal snapshot decoder feature does not match its field.");
        if (((featureFlags & SearchFeature) != 0) != hasSearch)
            throw new InvalidDataException("The terminal snapshot search feature does not match its field.");
        if (((featureFlags & HighlightRulesFeature) != 0) != hasHighlightRules)
            throw new InvalidDataException("The terminal snapshot highlight feature does not match its field.");
        if (minor == 0 && (featureFlags & ~(PendingUtf8Feature | SearchFeature | HighlightRulesFeature)) != 0)
            throw new InvalidDataException("The terminal snapshot has unsupported feature flags.");
        return new(
            buildId,
            nativeSnapshot,
            pendingUtf8,
            searchQuery,
            searchCaseSensitive,
            searchRegularExpression,
            highlightRules);
    }

    private static void WriteField(BinaryWriter writer, ushort id, ReadOnlySpan<byte> value)
    {
        writer.Write(id);
        writer.Write((ushort)0);
        writer.Write(checked((uint)value.Length));
        writer.Write(value);
    }

    private static void DecodeSearch(
        ReadOnlySpan<byte> field,
        out string query,
        out bool caseSensitive,
        out bool regularExpression)
    {
        if (field.Length < 8)
            throw new InvalidDataException("The terminal search field is truncated.");
        caseSensitive = ReadBoolean(field[0], "search case flag");
        regularExpression = ReadBoolean(field[1], "search regular-expression flag");
        if (BinaryPrimitives.ReadUInt16LittleEndian(field[2..]) != 0)
            throw new InvalidDataException("The terminal search reserved field is invalid.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(field[4..]);
        if (length > MaximumSearchLength || length != field.Length - 8)
            throw new InvalidDataException("The terminal search query length is invalid.");
        query = DecodeUtf8(field[8..], "search query");
    }

    private static IReadOnlyList<NativeTerminalApi.HighlightRulePayload> DecodeHighlightRules(ReadOnlySpan<byte> field)
    {
        if (field.Length < 4)
            throw new InvalidDataException("The terminal highlight field is truncated.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(field);
        if (count > MaximumHighlightRules)
            throw new InvalidDataException("The terminal highlight rule count is invalid.");
        var rules = new List<NativeTerminalApi.HighlightRulePayload>(checked((int)count));
        var offset = 4;
        for (var index = 0; index < count; index++)
        {
            const int fixedLength = 28;
            if (field.Length - offset < fixedLength)
                throw new InvalidDataException("A terminal highlight rule is truncated.");
            var id = BinaryPrimitives.ReadUInt64LittleEndian(field[offset..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(field[(offset + 8)..]);
            var foreground = BinaryPrimitives.ReadUInt32LittleEndian(field[(offset + 12)..]);
            var background = BinaryPrimitives.ReadUInt32LittleEndian(field[(offset + 16)..]);
            var priority = BinaryPrimitives.ReadInt32LittleEndian(field[(offset + 20)..]);
            var patternLength = BinaryPrimitives.ReadUInt32LittleEndian(field[(offset + 24)..]);
            offset += fixedLength;
            if ((flags & ~7u) != 0 || patternLength > MaximumPatternLength || patternLength > field.Length - offset)
                throw new InvalidDataException("A terminal highlight rule is invalid.");
            var pattern = DecodeUtf8(field.Slice(offset, checked((int)patternLength)), "highlight pattern");
            offset += checked((int)patternLength);
            rules.Add(new(id, pattern, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, foreground, background, priority));
        }
        if (offset != field.Length)
            throw new InvalidDataException("The terminal highlight field has trailing data.");
        return rules;
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> value, string name)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(value);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"The terminal snapshot {name} is not valid UTF-8.", exception);
        }
    }

    private static bool ReadBoolean(byte value, string name) => value switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException($"The terminal snapshot {name} is invalid."),
    };

    private static uint Crc32(ReadOnlySpan<byte> value)
    {
        var crc = uint.MaxValue;
        foreach (var item in value)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
