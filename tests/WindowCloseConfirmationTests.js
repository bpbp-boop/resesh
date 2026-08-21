const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const source = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "MainWindow.xaml.cs"),
  "utf8");

test("all window-close actions use the shared open-session confirmation guard", () => {
  assert.match(source, /AppWindow\.Closing \+= AppWindow_Closing/);
  assert.match(source, /private void Exit_Click\([^)]*\) => Close\(\)/);

  const handler = source.match(
    /private void AppWindow_Closing[\s\S]*?\n    }\r?\n\r?\n    private async Task ConfirmWindowCloseAsync/)?.[0] ?? "";

  assert.match(handler, /if \(_closeConfirmed \|\| !ViewModel\.AllTabs\.Any\(\)\)/);
  assert.match(handler, /args\.Cancel = true/);
  assert.match(handler, /ConfirmWindowCloseAsync\(\)/);
});

test("the shared close dialog confirms exit unless no sessions remain", () => {
  const method = source.match(
    /private async Task ConfirmWindowCloseAsync[\s\S]*?\n    }\r?\n\r?\n    private void PinButton_Click/)?.[0] ?? "";

  assert.match(method, /if \(count == 0\)[\s\S]*?_closeConfirmed = true;[\s\S]*?Close\(\);[\s\S]*?return;/);
  assert.match(method, /Title = "Exit Resesh\?"/);
  assert.match(method, /Content = \$"Are you sure you want to exit\?/);
  assert.match(method, /PrimaryButtonText = "Exit"/);
  assert.match(method, /DefaultButton = ContentDialogButton\.Close/);
  assert.match(method, /_closeConfirmed = true/);
});
