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
  assert.match(method, /Title = "Exit resesh\?"/);
  assert.match(method, /Content = \$"Are you sure you want to exit\?/);
  assert.match(method, /PrimaryButtonText = "Exit"/);
  assert.match(method, /DefaultButton = ContentDialogButton\.Close/);
  assert.match(method, /_closeConfirmed = true/);
});

test("Y confirms session-close dialogs without becoming a global destructive shortcut", () => {
  const yHelper = source.match(
    /private static async Task<bool> ShowCloseConfirmationAsync[\s\S]*?\n    }\r?\n\r?\n    private async Task<bool> ConfirmAsync/)?.[0] ?? "";

  assert.match(yHelper, /dialog\.AddHandler\([\s\S]*?PreviewKeyDownEvent[\s\S]*?new KeyEventHandler[\s\S]*?handledEventsToo: true\)/);
  assert.match(yHelper, /args\.Key != VirtualKey\.Y/);
  assert.match(yHelper, /confirmedByKeyboard = true/);
  assert.match(yHelper, /dialog\.Hide\(\)/);
  assert.match(yHelper, /confirmedByKeyboard \|\| result == ContentDialogResult\.Primary/);

  const singleClose = source.match(
    /public async Task RequestCloseTabAsync[\s\S]*?\n    }\r?\n\r?\n    private async Task RequestCloseTmuxTabAsync/)?.[0] ?? "";
  assert.equal(singleClose.match(/acceptY: true/g)?.length, 2);

  const tmuxClose = source.match(
    /private async Task RequestCloseTmuxTabAsync[\s\S]*?\n    }\r?\n\r?\n    public async Task RequestCloseManyAsync/)?.[0] ?? "";
  assert.match(tmuxClose, /ShowCloseConfirmationAsync\(dialog\)/);

  const bulkClose = source.match(
    /public async Task RequestCloseManyAsync[\s\S]*?\n    }\r?\n\r?\n    private void CloseTabCore/)?.[0] ?? "";
  assert.match(bulkClose, /acceptY: true/);

  const windowClose = source.match(
    /private async Task ConfirmWindowCloseAsync[\s\S]*?\n    }\r?\n\r?\n    private void PinButton_Click/)?.[0] ?? "";
  assert.match(windowClose, /ShowCloseConfirmationAsync\(dialog\)/);

  const genericConfirm = source.match(
    /private async Task<bool> ConfirmAsync[\s\S]*?\n    }\r?\n}/)?.[0] ?? "";
  assert.match(genericConfirm, /bool acceptY = false/);
  assert.match(genericConfirm, /acceptY\s*\?\s*await ShowCloseConfirmationAsync\(dialog\)/);
});

test("closing a tab hands off to its successor before removing the closing view", () => {
  const closeCore = source.match(
    /private void CloseTabCore[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(closeCore,
    /_groupViews\[group\]\.CloseTerminal\(\s*tab\.View as UIElement,\s*\(\) => ViewModel\.DetachTab\(tab\),\s*\(\) => \(tab\.View as IDisposable\)\?\.Dispose\(\)\);/);
  assert.doesNotMatch(closeCore, /RemoveTerminal|ViewModel\.CloseTab/);

  const groupView = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml.cs"), "utf8");
  const closeTerminal = groupView.match(
    /public void CloseTerminal[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  // Selection settles before any terminal changes visibility.
  assert.match(closeTerminal,
    /_terminalVisibilityDeferred = true;[\s\S]*?detachTab\(\);[\s\S]*?_terminalVisibilityDeferred = false;[\s\S]*?SyncTerminalVisibility\(\);/);
  // A held closing view is removed only once its successor has painted.
  assert.match(closeTerminal, /ReferenceEquals\(view, _heldTerminal\)\)\s*_heldTerminalReleased \+= Remove;/);
  // The WebView goes transparent before it leaves the tree, then is disposed.
  assert.match(closeTerminal, /view\.Opacity = 0;[\s\S]*?TerminalHost\.Children\.Remove\(view\);[\s\S]*?disposeTab\(\);/);

  const hide = groupView.match(/private void HideTerminal[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(hide, /view\.Opacity = 0;[\s\S]*?_terminalCollapseTimer\.Start\(\);/);
  assert.doesNotMatch(hide, /Visibility\.Collapsed;/);
});

test("the terminal page reports its first frame after boot and after every re-show", () => {
  const page = fs.readFileSync(
    path.join(__dirname, "..", "src", "Terminal", "wwwroot", "terminal.html"), "utf8");
  assert.match(page, /document\.addEventListener\("visibilitychange"[\s\S]*?postPaintedWhenVisible\(\)/);
  assert.match(page, /host\.postMessage\(\{ type: "ready"[^\n]*\n\s*postPaintedWhenVisible\(\);/);

  const control = fs.readFileSync(
    path.join(__dirname, "..", "src", "Terminal", "TerminalControl.cs"), "utf8");
  assert.match(control, /_webView\.Opacity = 0;\s*Children\.Add\(_webView\);/);
  assert.match(control, /case "painted":\s*_webView\.Opacity = 1;\s*OnPainted\(\);/);
});
