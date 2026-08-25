using Resesh.Core.Layout;
using Resesh.Core.Models;
using Resesh.Core.Storage;

namespace Resesh.Core.Tests;

public sealed class SessionTreeInteractionTests
{
    [Fact]
    public void KeyboardFocus_SelectsFocusedItem()
    {
        var selected = new HashSet<string>();
        var selection = NewSelection(selected);
        var visible = new[] { "A", "B", "C" };
        selection.SelectOnly("A");

        selection.SelectForKeyboardFocus("B", visible, extendRange: false, preserveSelection: false);

        Assert.Equal(["B"], selection.Items);
        Assert.Equal(["B"], selected);
    }

    [Fact]
    public void ShiftKeyboardFocus_ExtendsFromStableAnchor()
    {
        var selected = new HashSet<string>();
        var selection = NewSelection(selected);
        var visible = new[] { "A", "B", "C", "D" };
        selection.SelectOnly("B");

        selection.SelectForKeyboardFocus("D", visible, extendRange: true, preserveSelection: false);
        selection.SelectForKeyboardFocus("C", visible, extendRange: true, preserveSelection: false);

        Assert.Equal(["B", "C"], selection.Items);
        Assert.Equal(["B", "C"], selected.Order());
    }

    [Fact]
    public void ControlKeyboardFocus_PreservesSelection()
    {
        var selected = new HashSet<string>();
        var selection = NewSelection(selected);
        var visible = new[] { "A", "B" };
        selection.SelectOnly("A");

        selection.SelectForKeyboardFocus("B", visible, extendRange: false, preserveSelection: true);

        Assert.Equal(["A"], selection.Items);
        Assert.Equal(["A"], selected);
    }

    [Fact]
    public void DragInsideSelection_ReturnsEntireCustomSelection()
    {
        var selected = new HashSet<string>();
        var selection = NewSelection(selected);
        selection.SelectOnly("A");
        selection.Toggle("B");

        var dragged = selection.BeginDrag("B");

        Assert.Equal(["A", "B"], dragged);
        Assert.Equal(["A", "B"], selection.Items);
    }

    [Fact]
    public void DragOutsideSelection_RetargetsSelection()
    {
        var selected = new HashSet<string>();
        var selection = NewSelection(selected);
        selection.SelectOnly("A");

        var dragged = selection.BeginDrag("B");

        Assert.Equal(["B"], dragged);
        Assert.Equal(["B"], selected);
    }

    [Fact]
    public void CanceledDrop_IsRejectedWithoutMoves()
    {
        var session = NewSession("A");

        var plan = SessionDropPlanner.Plan(
            dropSucceeded: false,
            [session],
            targetFolder: "",
            targetKind: SessionKind.Ssh);

        Assert.False(plan.Accepted);
        Assert.Empty(plan.SessionIds);
    }

    [Fact]
    public void SuccessfulDrop_MovesEverySelectedSessionInTargetScope()
    {
        var first = NewSession("A");
        var second = NewSession("B");
        var local = NewSession("A", SessionKind.Local);

        var plan = SessionDropPlanner.Plan(
            dropSucceeded: true,
            [first, second, local],
            targetFolder: "C",
            targetKind: SessionKind.Ssh);

        Assert.True(plan.Accepted);
        Assert.Equal([first.Id, second.Id], plan.SessionIds);
    }

    [Fact]
    public void SuccessfulNoOpDrop_RebuildsWithoutPersistingMoves()
    {
        var session = NewSession("A");

        var plan = SessionDropPlanner.Plan(
            dropSucceeded: true,
            [session],
            targetFolder: "a",
            targetKind: SessionKind.Ssh);

        Assert.True(plan.Accepted);
        Assert.Empty(plan.SessionIds);
    }

    private static OrderedSelection<string> NewSelection(HashSet<string> selected) =>
        new((item, isSelected) =>
        {
            if (isSelected)
                selected.Add(item);
            else
                selected.Remove(item);
        });

    private static Session NewSession(string folder, SessionKind kind = SessionKind.Ssh) =>
        new() { Name = Guid.NewGuid().ToString(), FolderPath = folder, Kind = kind };
}
