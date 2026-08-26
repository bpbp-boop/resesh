const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const app = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "App.xaml.cs"),
  "utf8");
const windowCode = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "MainWindow.xaml.cs"),
  "utf8");
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
    /MoveDraggedTabIntoGroup[\s\S]*?_host\.TransferTabToGroup\(\s*tab,\s*_dragSource!\._host,\s*Group,/);
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
