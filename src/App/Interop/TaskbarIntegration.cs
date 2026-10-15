using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Resesh.App.Interop;

internal static partial class TaskbarIntegration
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

    /// <summary>Diagnostic hook (DEBUG builds wire this to a trace log).</summary>
    public static Action<string>? TraceHook { get; set; }

    /// <summary>
    /// Tags the window with the app's identity and, when they fit, the relaunch command,
    /// name and icon used by taskbar pinning. Best effort: taskbar grouping is cosmetic,
    /// so a rejected property is traced and skipped — it must never stop a window opening.
    /// </summary>
    public static void ConfigureWindow(IntPtr hwnd, string command, string iconPath)
    {
        if (hwnd == IntPtr.Zero)
            return;

        var iconResource = $"{iconPath},0";
        var plan = TaskbarRelaunchPlan.For(command, iconResource);
        if (!plan.SetRelaunchCommand)
            TraceHook?.Invoke($"taskbar: relaunch command skipped ({command.Length} chars > {TaskbarRelaunchPlan.MaxPropertyLength})");
        else if (!plan.SetRelaunchIcon)
            TraceHook?.Invoke($"taskbar: relaunch icon skipped ({iconResource.Length} chars > {TaskbarRelaunchPlan.MaxPropertyLength})");

        IPropertyStore store;
        try
        {
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, PropertyStoreId, out var pointer));
            store = ComObjects.Wrap<IPropertyStore>(pointer);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            TraceHook?.Invoke($"taskbar: no window property store: {ex.Message}");
            return;
        }
        try
        {
            TrySetString(store, AppId, AppUserModelId, "app id");
            // Name and command go together: the shell ignores one without the other.
            if (plan.SetRelaunchCommand
                && TrySetString(store, RelaunchCommand, command, "relaunch command")
                && TrySetString(store, RelaunchDisplayName, "resesh", "relaunch name")
                && plan.SetRelaunchIcon)
                TrySetString(store, RelaunchIcon, iconResource, "relaunch icon");
            var hr = store.Commit();
            if (hr < 0)
                TraceHook?.Invoke($"taskbar: commit failed 0x{hr:X8}");
        }
        finally
        {
            ComObjects.Release(store);
        }
    }

    private static bool TrySetString(IPropertyStore store, PropertyKey key, string value, string what)
    {
        try
        {
            SetString(store, key, value);
            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            TraceHook?.Invoke($"taskbar: {what} rejected ({value.Length} chars): {ex.Message}");
            return false;
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
            Marshal.ThrowExceptionForHR(PropVariantClear(ref property));
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal readonly struct PropertyKey
    {
        public PropertyKey(string formatId, uint propertyId)
        {
            FormatId = new Guid(formatId);
            PropertyId = propertyId;
        }

        public readonly Guid FormatId;
        public readonly uint PropertyId;
    }

    // PROPVARIANT's union occupies 16 bytes on both supported 64-bit targets.
    // PropVariantClear zeros the full native structure, including the unused tail.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
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

    [GeneratedComInterface]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(in PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(in PropertyKey key, in PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(IntPtr hwnd, in Guid interfaceId, out IntPtr propertyStore);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant property);
}
