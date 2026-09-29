const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const root = path.join(__dirname, "..", "src", "App");
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), "utf8");
const presenter = read("ModalDialogPresenter.cs");
const windowCode = require("./support/mainWindowSource");

function csharpFiles(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory())
      return entry.name === "bin" || entry.name === "obj" ? [] : csharpFiles(fullPath);
    return entry.name.endsWith(".cs") ? [fullPath] : [];
  });
}

test("the modal presenter delegates directly to WinUI composition", () => {
  assert.match(presenter, /await dialog\.ShowAsync\(\)/);
  assert.doesNotMatch(presenter, /OpenDialogCounts|OpenStateChanged|SetHostVisible/);
});

test("main windows do not hide terminal surfaces around modal content", () => {
  assert.doesNotMatch(windowCode, /SetTerminalHostsVisible|ModalDialogPresenter_OpenStateChanged/);
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
      unguarded.push(`${path.relative(root, file)}:${index + 1}`);
    });
  }

  assert.deepEqual(unguarded, []);
});

test("workspace replacement confirmation reaches the shared modal presenter", () => {
  const openWorkspace = windowCode.match(
    /private async Task OpenWorkspaceAsync[\s\S]*?\n    }\r?\n\r?\n    private static void OpenWorkspaceInNewWindow/,
  )?.[0] ?? "";
  const confirm = windowCode.match(/private Task<bool> ConfirmAsync\([\s\S]*?;\r?\n/)?.[0] ?? "";
  const sharedConfirm = read("Dialogs", "ConfirmDialog.xaml.cs");

  assert.match(openWorkspace, /"Replace Current Layout\?"/);
  assert.match(openWorkspace, /ConfirmAsync\(/);
  assert.match(confirm, /ConfirmDialog\.ConfirmAsync\(/);
  assert.match(sharedConfirm, /await this\.ShowModalAsync\(\)/);
});
