using System.Runtime.InteropServices;
using System.Text;

namespace Resesh.App.Interop;

internal static partial class TaskbarIntegration
{
    private static readonly PropertyKey Title = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9", 2);

    /// <summary>
    /// Replaces the taskbar and Start jump list with <paramref name="plan"/>, plus a New Window
    /// task. The classic destination list needs only the app user model id, not package
    /// identity. Returns the arguments of items the user removed from the list since the last
    /// update: the shell refuses a category that adds one back, so the caller must drop them.
    /// Best effort, like the rest of taskbar integration: a failure is traced and skipped.
    /// </summary>
    public static IReadOnlyList<string> UpdateJumpList(JumpListPlan plan, string executablePath)
    {
        ICustomDestinationList? list = null;
        try
        {
            list = (ICustomDestinationList)new DestinationList();
            list.SetAppID(AppUserModelId);
            var removedId = typeof(IObjectArray).GUID;
            list.BeginList(out _, ref removedId, out var removedObjects);
            var removed = RemovedArguments((IObjectArray)removedObjects);
            Marshal.ReleaseComObject(removedObjects);

            AppendCategory(list, "Pinned sessions", plan.Pinned, removed, executablePath);
            AppendCategory(list, "Recent sessions", plan.Recent, removed, executablePath);
            var tasks = Collection([new JumpListItem("New window", "", "Open another resesh window")], executablePath);
            list.AddUserTasks((IObjectArray)tasks);
            Marshal.ReleaseComObject(tasks);
            list.CommitList();
            return removed.ToList();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or UnauthorizedAccessException)
        {
            TraceHook?.Invoke($"jump list: update failed: {ex.Message}");
            try { list?.AbortList(); }
            catch (COMException) { }
            return [];
        }
        finally
        {
            if (list is not null)
                Marshal.ReleaseComObject(list);
        }
    }

    private static void AppendCategory(
        ICustomDestinationList list, string name, IReadOnlyList<JumpListItem> items,
        HashSet<string> removed, string executablePath)
    {
        var kept = items.Where(item => !removed.Contains(item.Arguments)).ToList();
        if (kept.Count == 0)
            return;
        var collection = Collection(kept, executablePath);
        try
        {
            list.AppendCategory(name, (IObjectArray)collection);
        }
        catch (COMException ex)
        {
            // E_ACCESSDENIED when the user turned off recent items in Start settings.
            TraceHook?.Invoke($"jump list: category '{name}' rejected: {ex.Message}");
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }
    }

    private static IObjectCollection Collection(IEnumerable<JumpListItem> items, string executablePath)
    {
        var collection = (IObjectCollection)new EnumerableObjectCollection();
        foreach (var item in items)
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(executablePath);
            link.SetArguments(item.Arguments);
            link.SetDescription(item.Description);
            link.SetIconLocation(executablePath, 0);
            var store = (IPropertyStore)link;
            SetString(store, Title, item.Title);
            Marshal.ThrowExceptionForHR(store.Commit());
            collection.AddObject(link);
            Marshal.ReleaseComObject(link);
        }
        return collection;
    }

    private static HashSet<string> RemovedArguments(IObjectArray removed)
    {
        var arguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        removed.GetCount(out var count);
        var linkId = typeof(IShellLinkW).GUID;
        for (uint i = 0; i < count; i++)
        {
            removed.GetAt(i, ref linkId, out var item);
            if (item is IShellLinkW link)
            {
                var buffer = new StringBuilder(1024);
                link.GetArguments(buffer, buffer.Capacity);
                arguments.Add(buffer.ToString());
            }
            Marshal.ReleaseComObject(item);
        }
        return arguments;
    }

    [ComImport]
    [Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    private class DestinationList;

    [ComImport]
    [Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
    private class EnumerableObjectCollection;

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void BeginList(out uint minSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IObjectArray items);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object removed);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? appId);
        void AbortList();
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
    }

    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        // IObjectArray
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);

        void AddObject([MarshalAs(UnmanagedType.Interface)] object item);
        void AddFromArray(IObjectArray items);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
