using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Sessions.Core.Credentials;

/// <summary>
/// Windows Credential Manager backend (CredRead/CredWrite/CredDelete P/Invoke).
/// Secrets are stored as generic credentials named "Sessions:{sessionId}".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialService : ICredentialService
{
    private const string SessionTargetPrefix = "Sessions:";
    private const string KeyTargetPrefix = "Sessions:Key:";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string? Read(Guid sessionId) => ReadTarget(SessionTarget(sessionId));

    public string? ReadKey(Guid keyId) => ReadTarget(KeyTarget(keyId));

    private static string? ReadTarget(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var credPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return null;
            throw new Win32Exception(error);
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                return "";
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public void Write(Guid sessionId, string secret) => WriteTarget(SessionTarget(sessionId), sessionId, secret);

    public void WriteKey(Guid keyId, string secret) => WriteTarget(KeyTarget(keyId), keyId, secret);

    private static void WriteTarget(string target, Guid id, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlob = blobPtr,
                CredentialBlobSize = (uint)blob.Length,
                Persist = CredPersistLocalMachine,
                UserName = id.ToString("D"),
            };
            if (!CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public void Delete(Guid sessionId) => DeleteTarget(SessionTarget(sessionId));

    public void DeleteKey(Guid keyId) => DeleteTarget(KeyTarget(keyId));

    private static void DeleteTarget(string target)
    {
        if (!CredDelete(target, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
                throw new Win32Exception(error);
        }
    }

    private static string SessionTarget(Guid sessionId) => SessionTargetPrefix + sessionId.ToString("D");

    private static string KeyTarget(Guid keyId) => KeyTargetPrefix + keyId.ToString("D");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
