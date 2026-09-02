const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const root = path.join(__dirname, "..", "src", "App");
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), "utf8");
const presenter = read("ModalDialogPresenter.cs");
const windowCode = read("MainWindow.xaml.cs");

function csharpFiles(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory())
      return entry.name === "bin" || entry.name === "obj" ? [] : csharpFiles(fullPath);
    return entry.name.endsWith(".cs") ? [fullPath] : [];
  });
}

test("the modal presenter tracks each XamlRoot and always releases suppression", () => {
  assert.match(presenter, /Dictionary<XamlRoot, int> OpenDialogCounts/);
  assert.match(presenter, /var xamlRoot = dialog\.XamlRoot[\s\S]*?Enter\(xamlRoot\);[\s\S]*?try[\s\S]*?dialog\.ShowAsync\(\)[\s\S]*?finally[\s\S]*?Exit\(xamlRoot\);/);
  assert.match(presenter, /OpenStateChanged\?\.Invoke\(xamlRoot, true\)/);
  assert.match(presenter, /if \(count > 1\)[\s\S]*?count - 1[\s\S]*?OpenDialogCounts\.Remove\(xamlRoot\);[\s\S]*?OpenStateChanged\?\.Invoke\(xamlRoot, false\)/);
});

test("each main window hides only its own native terminal hosts", () => {
  assert.match(windowCode, /ModalDialogPresenter\.OpenStateChanged \+= ModalDialogPresenter_OpenStateChanged/);
  assert.match(windowCode, /ModalDialogPresenter\.OpenStateChanged -= ModalDialogPresenter_OpenStateChanged/);
  assert.match(
    windowCode,
    /ModalDialogPresenter_OpenStateChanged\(XamlRoot xamlRoot, bool isOpen\)[\s\S]*?ReferenceEquals\(xamlRoot, Root\.XamlRoot\)[\s\S]*?SetTerminalHostsVisible\(!isOpen\)/,
  );
});

test("every direct ContentDialog display uses the modal presenter", () => {
  const unguarded = [];
  for (const file of csharpFiles(root)) {
    if (path.basename(file) === "ModalDialogPresenter.cs")
      continue;

    const lines = fs.readFileSync(file, "utf8").split(/\r?\n/);
    lines.forEach((line, index) => {
      if (!line.includes(".ShowAsync("))
        return;
      if (line.includes("GlobalSettingsDialog.ShowAsync(") || line.includes("SshKeyManagerDialog.ShowAsync("))
        return;
      unguarded.push(`${path.relative(root, file)}:${index + 1}`);
    });
  }

  assert.deepEqual(unguarded, []);
});

test("workspace replacement confirmation reaches the shared modal presenter", () => {
  const openWorkspace = windowCode.match(
    /private async Task OpenWorkspaceAsync[\s\S]*?\n    }\r?\n\r?\n    private static void OpenWorkspaceInNewWindow/,
  )?.[0] ?? "";
  const confirm = windowCode.match(
    /private async Task<bool> ConfirmAsync[\s\S]*?\n    }\r?\n}/,
  )?.[0] ?? "";

  assert.match(openWorkspace, /"Replace Current Layout\?"/);
  assert.match(openWorkspace, /ConfirmAsync\(/);
  assert.match(confirm, /dialog\.ShowModalAsync\(\)/);
});
