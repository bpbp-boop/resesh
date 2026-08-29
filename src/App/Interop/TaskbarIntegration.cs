using System.Runtime.InteropServices;

namespace Resesh.App.Interop;

internal static class TaskbarIntegration
{
    public const string AppUserModelId = "Resesh.Terminal";

    private static readonly Guid PropertyStoreId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey AppId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 5);
    private static readonly PropertyKey RelaunchCommand = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 2);
    private static readonly PropertyKey RelaunchDisplayName = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 4);
    private static readonly PropertyKey RelaunchIcon = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 3);

    public static void SetProcessIdentity()
    {
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppUserModelId));
    }

    public static void ConfigureWindow(IntPtr hwnd, string command, string iconPath)
    {
        if (hwnd == IntPtr.Zero)
            return;

        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, PropertyStoreId, out var store));
        try
        {
            SetString(store, AppId, AppUserModelId);
            SetString(store, RelaunchCommand, command);
            SetString(store, RelaunchDisplayName, "resesh");
            SetString(store, RelaunchIcon, $"{iconPath},0");
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static void SetString(IPropertyStore store, PropertyKey key, string value)
    {
        var property = PropVariant.FromString(value);
        try
        {
            Marshal.ThrowExceptionForHR(store.SetValue(key, property));
        }
        finally
        {
            PropVariantClear(property);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey
    {
        public PropertyKey(string formatId, uint propertyId)
        {
            FormatId = new Guid(formatId);
            PropertyId = propertyId;
        }

        public readonly Guid FormatId;
        public readonly uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private sealed class PropVariant
    {
        [FieldOffset(0)]
        private ushort _variantType;

        [FieldOffset(8)]
        private IntPtr _pointer;

        public static PropVariant FromString(string value) => new()
        {
            _variantType = 31, // VT_LPWSTR
            _pointer = Marshal.StringToCoTaskMemUni(value),
        };
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(in PropertyKey key, [Out] PropVariant value);

        [PreserveSig]
        int SetValue(in PropertyKey key, [In] PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        in Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void PropVariantClear([In, Out] PropVariant property);
}
