using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Resesh.Core.Storage;

namespace Resesh.Core.Credentials;

/// <summary>
/// Secrets for a profile that must not touch Windows Credential Manager (a --data-dir run):
/// one JSON file in the data folder, each secret encrypted with DPAPI for the current Windows
/// user. It saves like the other stores: atomically, never over a file it could not read,
/// and the in-memory copy changes only after the write succeeds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProtectedFileCredentialService : ICredentialService
{
    public const string FileName = "credentials.json";

    // Ties the blobs to this purpose, so another DPAPI consumer cannot decrypt them by accident.
    private static readonly byte[] Entropy = "resesh credentials v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = CoreJson.With(new() { WriteIndented = true });

    private readonly StoreFile _file;
    private readonly object _gate = new();
    private Dictionary<Guid, string>? _secrets;

    public ProtectedFileCredentialService(string path)
    {
        _file = new StoreFile(path, "Saved passwords");
    }

    /// <summary>Set once the file is first read when it was recovered or unreadable.</summary>
    public string? LoadWarning
    {
        get { lock (_gate) { EnsureLoaded(); return _file.Warning; } }
    }

    public string? Read(Guid sessionId)
    {
        string? stored;
        lock (_gate)
            stored = EnsureLoaded().GetValueOrDefault(sessionId);
        if (stored is null)
            return null;
        try
        {
            return Encoding.Unicode.GetString(Unprotect(Convert.FromBase64String(stored)));
        }
        catch (Exception exception) when (exception is FormatException or Win32Exception)
        {
            // Saved by another Windows user or machine: as good as no saved password.
            return null;
        }
    }

    public bool Has(Guid sessionId)
    {
        lock (_gate)
            return EnsureLoaded().ContainsKey(sessionId);
    }

    public void Write(Guid sessionId, string secret)
    {
        CredentialStoreException.ThrowIfTooLong(secret);
        var stored = Convert.ToBase64String(Protect(Encoding.Unicode.GetBytes(secret)));
        lock (_gate)
        {
            var candidate = new Dictionary<Guid, string>(EnsureLoaded()) { [sessionId] = stored };
            Save(candidate);
        }
    }

    public void Delete(Guid sessionId)
    {
        lock (_gate)
        {
            var current = EnsureLoaded();
            if (!current.ContainsKey(sessionId))
                return;
            var candidate = new Dictionary<Guid, string>(current);
            candidate.Remove(sessionId);
            Save(candidate);
        }
    }

    private Dictionary<Guid, string> EnsureLoaded() =>
        _secrets ??= _file.Load(ReadFile) ?? [];

    private void Save(Dictionary<Guid, string> candidate)
    {
        _file.Write(JsonSerializer.Serialize(candidate, JsonOptions.Info<Dictionary<Guid, string>>()));
        _secrets = candidate;
    }

    private static Dictionary<Guid, string>? ReadFile(string path)
    {
        var data = JsonSerializer.Deserialize(File.ReadAllText(path), JsonOptions.Info<Dictionary<Guid, string>>());
        if (data?.Values.Any(value => value is null) == true)
            throw new InvalidDataException("The saved passwords file contains an empty entry.");
        return data;
    }

    private static byte[] Protect(byte[] plaintext)
    {
        try
        {
            return Transform(plaintext, protect: true);
        }
        catch (Win32Exception exception)
        {
            throw new CredentialStoreException($"The password could not be encrypted: {exception.Message}", exception);
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    private static byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var source = new DataBlob { Size = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            DataBlob result;
            var ok = protect
                ? CryptProtectData(ref source, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out result)
                : CryptUnprotectData(ref source, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out result);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var output = new byte[result.Size];
                Marshal.Copy(result.Data, output, 0, result.Size);
                return output;
            }
            finally
            {
                Marshal.Copy(new byte[result.Size], 0, result.Data, result.Size);
                LocalFree(result.Data);
            }
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
        }
    }

    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
