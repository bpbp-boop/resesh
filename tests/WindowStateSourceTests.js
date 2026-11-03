const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

// Window state that used to live in more than one place: each check pins the one source.
const windowCode = require("./support/mainWindowSource");
const read = (...parts) => fs.readFileSync(path.join(__dirname, "..", ...parts), "utf8");
const tabGroup = read("src", "App", "Controls", "TabGroupView.xaml.cs");
const app = read("src", "App", "App.xaml.cs");
const catalog = read("src", "App", "ViewModels", "AppCommandCatalog.cs");
const coordinator = read("src", "Core", "Storage", "WorkspaceCoordinator.cs");
const mainXaml = read("src", "App", "MainWindow.xaml");

const member = (source, signature) =>
  source.match(new RegExp(`${signature}[\\s\\S]*?\\n    }\\r?\\n`))?.[0] ?? "";

test("pane sizes live in the split layout, not in a map beside it or in the grids", () => {
  assert.doesNotMatch(windowCode, /_savedPaneSizes|CaptureLivePaneSizes|\.PaneSizes\b/);
  // Grids are built from the branch's sizes and write a resize straight back to it.
  const build = member(windowCode, "private FrameworkElement BuildGroupLayoutElement");
  assert.match(build, /branch\.Sizes/);
  assert.match(build, /splitter\.ManipulationCompleted \+= \(_, _\) => RecordPaneSizes\(grid, branch\)/);
  assert.match(build, /KeyUpEvent[\s\S]*?RecordPaneSizes\(grid, branch\)/);
  assert.match(member(windowCode, "private static void RecordPaneSizes"), /branch\.SetSizes\(sizes\)/);
  // Saving, zooming and parking read the layout; none of them reads a grid.
  const capture = member(windowCode, "private static WorkspaceLayoutNode CaptureLayoutNode");
  assert.match(capture, /Sizes = branch\.Sizes\.ToList\(\)/);
  assert.doesNotMatch(capture, /FrameworkElement|Grid/);
  assert.match(windowCode, /private static WorkspaceLayout CaptureSplitLayout\(SplitLayout<TabGroupViewModel> layout\)/);
});

test("a parked pane's selection never moves focus, and focus only goes to a shown pane", () => {
  const selection = member(tabGroup, "private void Tabs_SelectionChanged");
  assert.match(selection, /if \(!_isParked\)\s*_host\.FocusGroupForSelection\(Group\);/);
  assert.doesNotMatch(selection, /_host\.FocusGroup\(/);

  const focus = member(windowCode, "public void FocusGroup\\(TabGroupViewModel group\\)");
  assert.match(focus, /ParkedWorkspaceOf\(group\) is not \{ \} workspace\)\s*return;/);
  assert.match(focus, /if \(!ViewModel\.Groups\.Contains\(group\)\)\s*return;/);
  // The queued re-assert yields to any later explicit choice.
  assert.match(focus, /var intent = \+\+_focusIntent;[\s\S]*?intent != _focusIntent/);
  assert.match(member(windowCode, "public void FocusGroupForSelection"), /ViewModel\.Groups\.Contains\(group\)/);
  assert.doesNotMatch(member(windowCode, "private void FinishLayoutChange"), /TryEnqueue/);
});

test("keyboard focus only lands in the pane that is focused when it is given", () => {
  const terminal = member(tabGroup, "private void FocusTerminal");
  assert.match(terminal, /TryEnqueue\([\s\S]*?ReferenceEquals\(_host\.ViewModel\.FocusedGroup, Group\)[\s\S]*?view\.FocusTerminal\(\)/);
  const content = member(windowCode, "private void FocusTabContent");
  assert.match(content, /TryEnqueue\([\s\S]*?ReferenceEquals\(ViewModel\.TryGroupOf\(tab\), ViewModel\.FocusedGroup\)/);
  assert.match(member(windowCode, "private void FocusActiveTerminal"), /FocusTabContent\(tab, userInitiated: true\)/);
});

test("tabs move between panes through one primitive", () => {
  assert.doesNotMatch(windowCode, /PlaceZoomTab|PlaceWorkspaceTab|RemoveWorkspaceTab/);
  const place = member(windowCode, "private void PlaceTab");
  assert.match(place, /source\.RemoveTab\(tab\);/);
  assert.match(place, /RemoveTerminal\(view\)[\s\S]*?AddTerminal\(view\)/);
  assert.match(member(windowCode, "public void MoveTabBetweenGroups"), /PlaceTab\(tab, targetGroup, targetIndex, select: true\)/);
  // Outside PlaceTab, nothing takes a tab out of a pane's list by hand.
  assert.doesNotMatch(windowCode, /\.Tabs\.Remove\(tab\)/);
});

test("workspace actions share one rule and capture the workspace they name", () => {
  // Add to current layout: the title-bar menu, the rail, the Workspaces pane and the palette.
  assert.match(member(windowCode, "internal void RefreshWorkspaceMenu"), /if \(CanAddWorkspaceToLayout\(workspace\.Id\)\)/);
  assert.match(member(windowCode, "private MenuFlyout BuildWorkspaceRailMenu"), /if \(CanAddWorkspaceToLayout\(workspace\.Id\)\)/);
  assert.match(member(windowCode, "private void WorkspaceRowMenu_Opening"), /"additive" => [\s\S]*?CanAddWorkspaceToLayout\(item\.Id\)/);
  assert.match(member(windowCode, "private void WorkspaceRowMenu_Opening"), /"update" => [\s\S]*?CanUpdateWorkspace\(item\.Id\)/);
  assert.match(mainXaml, /<MenuFlyout Opening="WorkspaceRowMenu_Opening">/);
  assert.match(catalog, /if \(services\.CanAddWorkspaceToLayout\(workspace\.Id\)\)\s*yield return new\("Workspaces", Fixed\(\$"Open \{workspace\.Name\} Additively"\)/);
  assert.match(member(windowCode, "private Task OpenWorkspaceAsync"), /if \(!CanAddWorkspaceToLayout\(workspace\.Id\)\)/);

  // The coordinator is given a workspace's live layout by id, never "what is shown".
  assert.match(coordinator, /Func<Guid, WorkspaceLayout\?> liveLayout/);
  assert.match(coordinator, /public bool Update\(Guid id\) => liveLayout\(id\) is \{ \} layout && Save\(id, layout\);/);
  assert.match(windowCode, /new\(App\.Workspaces, LiveWorkspaceLayoutOrNull,/);
});

test("the window title names the active tab the way its tab header does", () => {
  const title = member(windowCode, "internal void RefreshWindowTitle");
  assert.match(title, /tab\.Header/);
  assert.match(title, /ActiveTab\?\.Header/);
  assert.doesNotMatch(title, /Session\.Name/);
});

test("the pinned sessions list follows Home's pinned tabs, including when a window closes", () => {
  assert.match(member(app, "internal static void SavePinnedSessions"), /PinnedSessions\.ToSave\([\s\S]*?window\.HomeTabs[\s\S]*?app\._windows\.Count\)/);
  assert.match(app, /_windows\.Remove\(window\);\s*QueuePinnedSessionsSave\(\);/);
  // A closing window saves before its tabs go, so the last one keeps a change still queued.
  assert.match(windowCode, /SaveWorkspacesOnClose,\s*App\.SavePinnedSessions,\s*ViewModel\.CloseAllTabs[,)]/);
  assert.match(windowCode, /group\.Tabs\.CollectionChanged \+= \(_, _\) =>[\s\S]*?App\.QueuePinnedSessionsSave\(\);/);
  assert.match(windowCode, /nameof\(TabViewModel\.IsPinned\)\)\s*App\.QueuePinnedSessionsSave\(\);/);
  assert.match(member(windowCode, "public void RestorePinnedSessions"), /PinnedSessions\.ToRestore\(ids, App\.Workspaces\.Workspaces\)/);
});
