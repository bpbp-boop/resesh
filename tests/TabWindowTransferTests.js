const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const app = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "App.xaml.cs"),
  "utf8");
const windowCode = require("./support/mainWindowSource");
const tabGroup = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml.cs"),
  "utf8");
const mainViewModel = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "ViewModels", "MainViewModel.cs"),
  "utf8");

test("a tab drag exposes drop targets in every app window", () => {
  assert.match(
    app,
    /SetTabContentDropTargetsVisible\(bool visible\)[\s\S]*?foreach \(var window in app\._windows\.ToList\(\)\)[\s\S]*?window\.SetTabContentDropTargetsVisibleCore\(visible\)/);
  assert.match(
    windowCode,
    /public void SetTabContentDropTargetsVisible\(bool visible\) =>\s*App\.SetTabContentDropTargetsVisible\(visible\)/);
});

test("a strip drop transfers the tab from its source window", () => {
  assert.match(
    tabGroup,
    /MoveDraggedTabIntoGroup[\s\S]*?_host\.TransferTabToGroup\(\s*drag\.Tab,\s*drag\.Source\._host,\s*Group,/);
  assert.match(
    tabGroup,
    /ContentDropSurface_Drop[\s\S]*?!ReferenceEquals\(sourceHost, _host\)[\s\S]*?_host\.TransferTabToGroup\(tab, sourceHost, Group, Group\.Tabs\.Count\)[\s\S]*?_host\.SplitTab\(tab, Group, direction\)/);
});

test("cross-window transfer preserves the live tab and reparents its terminal view", () => {
  const transfer = windowCode.match(
    /public void TransferTabToGroup\([\s\S]*?\n    }\r?\n\r?\n    public void DetachTabForTransfer/)?.[0] ?? "";
  const detach = windowCode.match(
    /public void DetachTabForTransfer\([\s\S]*?\n    }\r?\n\r?\n    private void CollapseGroupIfEmpty/)?.[0] ?? "";

  assert.match(transfer, /sourceHost\.DetachTabForTransfer\(tab\)/);
  assert.match(transfer, /ViewModel\.AttachTab\(tab, targetGroup, targetIndex\)/);
  assert.match(transfer, /_groupViews\[targetGroup\]\.AddTerminal\(view\)/);
  assert.match(detach, /_groupViews\[sourceGroup\]\.RemoveTerminal\(view\)/);
  assert.match(detach, /ViewModel\.DetachTab\(tab\)/);
  assert.doesNotMatch(detach, /CloseTab|Dispose/);
});

test("view-model ownership subscriptions move with a transferred tab", () => {
  const attach = mainViewModel.match(
    /public void AttachTab\([\s\S]*?\n    }\r?\n\r?\n    \/\/\/ <summary>Detaches/)?.[0] ?? "";
  const detach = mainViewModel.match(
    /public TabGroupViewModel DetachTab\([\s\S]*?\n    }\r?\n\r?\n    private void Tab_PropertyChanged/)?.[0] ?? "";

  assert.match(attach, /tab\.PropertyChanged \+= Tab_PropertyChanged/);
  assert.match(detach, /tab\.PropertyChanged -= Tab_PropertyChanged/);
  assert.doesNotMatch(detach, /Dispose/);
});

const tabDrag = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.Drag.cs"),
  "utf8");
const tabMotion = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.Motion.cs"),
  "utf8");

// A tab drag is one session object. Statics cleared on separate paths stayed set when the
// source left the tree mid-drag, keeping every window's drop targets up.
test("a tab drag is one session that ends when its source leaves the tree", () => {
  assert.match(tabDrag, /private static TabDragSession\? _drag;/);
  assert.doesNotMatch(tabGroup, /private static (?:TabViewModel|TabGroupView)\? _\w+;/);
  // Handlers read the session through CurrentDrag, which ignores a source that is gone.
  assert.match(tabDrag, /CurrentDrag => _drag is \{ IsSourceGone: false \} drag \? drag : null/);
  assert.match(tabDrag, /IsSourceGone => VisualTreeHelper\.GetParent\(Source\) is null/);

  // Ending clears the session before the source resets, then hides every window's targets.
  const end = tabDrag.match(/private void EndTabDrag\(\)[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(end, /_drag = null;[\s\S]*?drag\?\.Source\.ResetTabDragPreview\(\);[\s\S]*?_host\.SetTabContentDropTargetsVisible\(false\);/);

  // A source unloaded (and still out of the tree a turn later) or parked ends its drag.
  const unloaded = tabDrag.match(/private void TabGroupView_Unloaded[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(unloaded, /DispatcherQueue\.TryEnqueue[\s\S]*?VisualTreeHelper\.GetParent\(this\) is not null[\s\S]*?_drag\?\.Source == this[\s\S]*?EndTabDrag\(\)/);
  assert.match(unloaded, /ReleaseTabStripHolds\(\)/);
  assert.match(tabGroup, /Unloaded \+= TabGroupView_Unloaded;/);
  assert.match(tabGroup, /PointerCaptureLostEvent, new PointerEventHandler\(Tabs_PointerCaptureLost\), true/);
  const parked = tabGroup.match(/public bool IsParked[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(parked, /if \(value\)[\s\S]*?_drag\?\.Source == this[\s\S]*?EndTabDrag\(\)[\s\S]*?ReleaseTabStripHolds\(\)/);

  // Completion can reach the source before the target's drop: the session lives on, marked
  // completing, for one dispatcher turn, and only that same session is ended then.
  const completed = tabGroup.match(/private void Tabs_TabDragCompleted[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(completed, /drag\.Phase = TabDragPhase\.Completing;/);
  assert.match(completed, /DispatcherQueue\.TryEnqueue\([\s\S]*?ReferenceEquals\(_drag, drag\)[\s\S]*?EndTabDrag\(\)/);

  // Held widths and pending slides are released when the strip leaves the screen.
  const release = tabMotion.match(/private void ReleaseTabStripHolds\(\)[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(release, /CancelTabMotion\(\);/);
  assert.match(release, /_tabWidthHoldPending = false;/);
});
