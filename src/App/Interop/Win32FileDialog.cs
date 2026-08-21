using System.Runtime.InteropServices;

namespace Resesh.App.Interop;

/// <summary>
/// Win32 open-file dialog (IFileOpenDialog). Used instead of the WinRT FileOpenPicker
/// where a default start folder is needed — WinRT pickers only support the fixed
/// PickerLocationId set, not arbitrary paths.
/// </summary>
internal static class Win32FileDialog
{
    private const int ErrorCancelled = unchecked((int)0x800704C7);
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>
    /// Shows an open-file dialog owned by <paramref name="owner"/>. The dialog remembers
    /// its own last-used folder per <paramref name="clientId"/>; <paramref name="defaultFolder"/>
    /// is used only when there is no last-used folder yet.
    /// Returns the picked path, or null if cancelled.
    /// </summary>
    public static string? PickFile(IntPtr owner, Guid clientId, string defaultFolder, string? title = null)
    {
        var dialog = (IFileDialog)new FileOpenDialogRCW();
        dialog.SetClientGuid(in clientId);
        if (title is not null)
            dialog.SetTitle(title);
        if (Directory.Exists(defaultFolder)
            && SHCreateItemFromParsingName(defaultFolder, IntPtr.Zero, typeof(IShellItem).GUID, out var folder) == 0)
            dialog.SetDefaultFolder(folder);

        var hr = dialog.Show(owner);
        if (hr == ErrorCancelled)
            return null;
        Marshal.ThrowExceptionForHR(hr);

        dialog.GetResult(out var item);
        item.GetDisplayName(SIGDN_FILESYSPATH, out var pathPtr);
        try
        {
            return Marshal.PtrToStringUni(pathPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        // Methods must stay in vtable order; unused ones are declared but never called.
        [PreserveSig] int Show(IntPtr hwndOwner);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(in Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, in Guid riid, out IShellItem ppv);
}
