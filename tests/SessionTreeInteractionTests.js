const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const xaml = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "MainWindow.xaml"),
  "utf8");
const code = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "MainWindow.xaml.cs"),
  "utf8");
const treeModel = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "ViewModels", "MainViewModel.cs"),
  "utf8");

test("session tree keeps ordinary folders normal-weight", () => {
  const folderTemplate = xaml.match(
    /<DataTemplate x:Key="FolderTemplate"[\s\S]*?<\/DataTemplate>/)?.[0] ?? "";
  const localRootTemplate = xaml.match(
    /<DataTemplate x:Key="LocalRootTemplate"[\s\S]*?<\/DataTemplate>/)?.[0] ?? "";

  assert.match(folderTemplate, /Text="\{x:Bind Name\}"[\s\S]*?FontWeight="Normal"/);
  assert.match(localRootTemplate, /Text="\{x:Bind Name\}"[\s\S]*?FontWeight="SemiBold"/);
});

test("custom selection owns focus and receives handled Enter presses", () => {
  assert.match(
    code,
    /SessionTree\.AddHandler\([\s\S]*?KeyDownEvent[\s\S]*?SessionTree_KeyDown[\s\S]*?handledEventsToo: true\)/);
  assert.match(
    code,
    /private void TreeNode_Tapped[\s\S]*?item\.Focus\(FocusState\.Pointer\)[\s\S]*?SelectOnly\(node\)/);
});

test("session drag persists after native reordering finishes", () => {
  const tree = xaml.match(/<TreeView\s+x:Name="SessionTree"[\s\S]*?<TreeView\.Resources>/)?.[0] ?? "";
  assert.match(tree, /CanDragItems="True"/);
  assert.match(tree, /CanReorderItems="True"/);
  assert.match(tree, /DragItemsCompleted="SessionTree_DragItemsCompleted"/);
  assert.doesNotMatch(tree, /DragOver="(?:SessionTree|TreeNode)_DragOver"/);
  assert.doesNotMatch(tree, /Drop="(?:SessionTree|TreeNode)_Drop"/);

  const handler = code.match(
    /private void SessionTree_DragItemsCompleted[\s\S]*?\n    }\r?\n\r?\n    \/\//)?.[0] ?? "";
  assert.match(handler, /args\.NewParentItem/);
  assert.match(handler, /var movedSessionIds = args\.Items/);
  assert.match(handler, /DispatcherQueuePriority\.Low/);
  assert.match(handler, /ViewModel\.MoveSessionsToFolder\(movedSessionIds, targetFolder\)/);
});

test("creating a folder preserves the realized tree", () => {
  const createFolder = treeModel.match(
    /public void CreateFolder[\s\S]*?\n    }\r?\n\r?\n    public void RenameFolder/)?.[0] ?? "";
  assert.match(createFolder, /InsertFolderPath\(path, kind\)/);
  assert.doesNotMatch(createFolder, /RebuildTree\(\)/);

  const insertFolder = treeModel.match(
    /private void InsertFolderPath[\s\S]*?\n    }\r?\n\r?\n    private static void InsertFolderSorted/)?.[0] ?? "";
  assert.match(insertFolder, /_currentNodes\.Add\(node\)/);
  assert.match(insertFolder, /InsertFolderSorted\(siblings, node\)/);
});
