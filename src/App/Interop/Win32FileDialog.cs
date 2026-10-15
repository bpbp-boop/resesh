using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Resesh.App.Interop;

/// <summary>
/// Win32 open-file dialog (IFileOpenDialog). Used instead of the WinRT FileOpenPicker
/// where a default start folder is needed — WinRT pickers only support the fixed
/// PickerLocationId set, not arbitrary paths.
/// </summary>
internal static partial class Win32FileDialog
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
        var dialog = ComObjects.Create<IFileDialog>(FileOpenDialogClass);
        IShellItem? folder = null;
        IShellItem? item = null;
        try
        {
            dialog.SetClientGuid(in clientId);
            if (title is not null)
                dialog.SetTitle(title);
            if (Directory.Exists(defaultFolder)
                && SHCreateItemFromParsingName(defaultFolder, IntPtr.Zero, typeof(IShellItem).GUID, out var folderPointer) == 0)
            {
                folder = ComObjects.Wrap<IShellItem>(folderPointer);
                dialog.SetDefaultFolder(folder);
            }

            var hr = dialog.Show(owner);
            if (hr == ErrorCancelled)
                return null;
            Marshal.ThrowExceptionForHR(hr);

            dialog.GetResult(out item);
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
        finally
        {
            ComObjects.Release(item);
            ComObjects.Release(folder);
            ComObjects.Release(dialog);
        }
    }

    private static readonly Guid FileOpenDialogClass = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    internal partial interface IFileDialog
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
        void SetFileName(string pszName);
        void GetFileName(out IntPtr pszName);
        void SetTitle(string pszTitle);
        void SetOkButtonLabel(string pszText);
        void SetFileNameLabel(string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension(string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(in Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [GeneratedComInterface, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    internal partial interface IShellItem
    {
        void BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, in Guid riid, out IntPtr ppv);
}
