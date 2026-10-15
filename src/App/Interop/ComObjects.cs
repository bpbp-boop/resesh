using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Resesh.App.Interop;

/// <summary>
/// Creates and releases shell COM objects through source-generated COM interop, which,
/// unlike [ComImport], also works when the app is compiled ahead of time.
/// </summary>
internal static partial class ComObjects
{
    private const uint ClassContextInprocServer = 1;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>CoCreateInstance for <paramref name="classId"/>, as interface <typeparamref name="T"/>.</summary>
    public static T Create<T>(Guid classId) where T : class
    {
        var interfaceId = typeof(T).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(classId, IntPtr.Zero, ClassContextInprocServer, interfaceId, out var pointer));
        return Wrap<T>(pointer);
    }

    /// <summary>Wraps a COM pointer the caller owns; the caller's reference is released.</summary>
    public static T Wrap<T>(IntPtr pointer) where T : class
    {
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>Releases the native object now rather than when the wrapper is collected.</summary>
    public static void Release(object? value) => (value as ComObject)?.FinalRelease();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr pointer);
}
