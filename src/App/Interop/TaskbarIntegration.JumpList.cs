using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

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
            list = ComObjects.Create<ICustomDestinationList>(DestinationListClass);
            list.SetAppID(AppUserModelId);
            var removedId = typeof(IObjectArray).GUID;
            list.BeginList(out _, removedId, out var removedPointer);
            var removedObjects = ComObjects.Wrap<IObjectArray>(removedPointer);
            var removed = RemovedArguments(removedObjects);
            ComObjects.Release(removedObjects);

            AppendCategory(list, "Pinned sessions", plan.Pinned, removed, executablePath);
            AppendCategory(list, "Recent sessions", plan.Recent, removed, executablePath);
            var tasks = Collection([new JumpListItem("New window", "", "Open another resesh window")], executablePath);
            list.AddUserTasks((IObjectArray)tasks);
            ComObjects.Release(tasks);
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
            ComObjects.Release(list);
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
            ComObjects.Release(collection);
        }
    }

    private static IObjectCollection Collection(IEnumerable<JumpListItem> items, string executablePath)
    {
        var collection = ComObjects.Create<IObjectCollection>(EnumerableObjectCollectionClass);
        foreach (var item in items)
        {
            var link = ComObjects.Create<IShellLinkW>(ShellLinkClass);
            link.SetPath(executablePath);
            link.SetArguments(item.Arguments);
            link.SetDescription(item.Description);
            link.SetIconLocation(executablePath, 0);
            var store = (IPropertyStore)link;
            SetString(store, Title, item.Title);
            Marshal.ThrowExceptionForHR(store.Commit());
            collection.AddObject(link);
            ComObjects.Release(link);
        }
        return collection;
    }

    private static HashSet<string> RemovedArguments(IObjectArray removed)
    {
        var arguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        removed.GetCount(out var count);
        var linkId = typeof(IShellLinkW).GUID;
        var buffer = new char[1024];
        for (uint i = 0; i < count; i++)
        {
            removed.GetAt(i, linkId, out var pointer);
            var link = ComObjects.Wrap<IShellLinkW>(pointer);
            link.GetArguments(buffer, buffer.Length);
            arguments.Add(new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length));
            ComObjects.Release(link);
        }
        return arguments;
    }

    private static readonly Guid DestinationListClass = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    private static readonly Guid EnumerableObjectCollectionClass = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    internal partial interface ICustomDestinationList
    {
        void SetAppID(string appId);
        void BeginList(out uint minSlots, in Guid riid, out IntPtr removed);
        void AppendCategory(string category, IObjectArray items);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations(in Guid riid, out IntPtr removed);
        void DeleteList(string? appId);
        void AbortList();
    }

    [GeneratedComInterface]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    internal partial interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, in Guid riid, out IntPtr item);
    }

    [GeneratedComInterface]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    internal partial interface IObjectCollection : IObjectArray
    {
        void AddObject(IShellLinkW item);
        void AddFromArray(IObjectArray items);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    // Only the vtable prefix up to SetPath is declared; unused methods take raw pointers.
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(IntPtr file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(IntPtr name, int maxName);
        void SetDescription(string name);
        void GetWorkingDirectory(IntPtr directory, int maxPath);
        void SetWorkingDirectory(string directory);
        void GetArguments([Out, MarshalUsing(CountElementName = nameof(maxPath))] char[] arguments, int maxPath);
        void SetArguments(string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(IntPtr iconPath, int maxPath, out int iconIndex);
        void SetIconLocation(string iconPath, int iconIndex);
        void SetRelativePath(string relativePath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath(string file);
    }
}
