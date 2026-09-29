const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const source = require("./support/mainWindowSource");

test("all window-close actions use the shared open-session confirmation guard", () => {
  assert.match(source, /AppWindow\.Closing \+= AppWindow_Closing/);
  // File > Exit closes the window like the title-bar button, so AppWindow_Closing guards it.
  const services = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "MainWindow.Services.cs"), "utf8");
  assert.match(services, /void IMainWindowServices\.CloseWindow\(\) => Close\(\);/);

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
  assert.match(method, /"Exit resesh\?"/);
  assert.match(method, /\$"Are you sure you want to exit\?/);
  assert.match(method, /"Exit",\s*acceptY: true/);
  assert.match(method, /_closeConfirmed = true/);
});

const confirmDialog = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Dialogs", "ConfirmDialog.xaml.cs"), "utf8");
const confirmXaml = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Dialogs", "ConfirmDialog.xaml"), "utf8");

test("Y confirms session-close dialogs without becoming a global destructive shortcut", () => {
  // Cancel is the default button, and only the open dialog hears Y.
  assert.match(confirmXaml, /DefaultButton="Close"/);
  const confirm = confirmDialog.match(/public async Task<bool> ConfirmAsync\(\)[\s\S]*?\n    }/)?.[0] ?? "";
  assert.match(confirm, /if \(AcceptsY\)[\s\S]*?AddHandler\([\s\S]*?PreviewKeyDownEvent[\s\S]*?new KeyEventHandler[\s\S]*?handledEventsToo: true\)/);
  assert.match(confirm, /args\.Key != VirtualKey\.Y/);
  assert.match(confirm, /confirmedByKeyboard = true/);
  assert.match(confirm, /Hide\(\)/);
  assert.match(confirm, /confirmedByKeyboard \|\| result == ContentDialogResult\.Primary/);

  const singleClose = source.match(
    /public async Task RequestCloseTabAsync[\s\S]*?\n    }\r?\n\r?\n    private async Task RequestCloseTmuxTabAsync/)?.[0] ?? "";
  assert.equal(singleClose.match(/acceptY: true/g)?.length, 2);

  const tmuxClose = source.match(
    /private async Task RequestCloseTmuxTabAsync[\s\S]*?\n    }\r?\n\r?\n    public async Task RequestCloseManyAsync/)?.[0] ?? "";
  assert.match(tmuxClose, /AcceptsY = true/);

  const bulkClose = source.match(
    /public async Task RequestCloseManyAsync[\s\S]*?\n    }\r?\n\r?\n    private void CloseTabCore/)?.[0] ?? "";
  assert.match(bulkClose, /acceptY: true/);

  const windowClose = source.match(
    /private async Task ConfirmWindowCloseAsync[\s\S]*?\n    }\r?\n\r?\n    private void PinButton_Click/)?.[0] ?? "";
  assert.match(windowClose, /acceptY: true/);

  const genericConfirm = source.match(/private Task<bool> ConfirmAsync\([\s\S]*?;\r?\n/)?.[0] ?? "";
  assert.match(genericConfirm, /bool acceptY = false/);
  assert.match(genericConfirm, /ConfirmDialog\.ConfirmAsync\(Root\.XamlRoot, title, message, primaryText, acceptY\)/);
});

test("bulk close can end persistent sessions and keeps tabs whose session survived", () => {
  const bulkTmux = source.match(
    /private async Task RequestCloseManyWithTmuxAsync[\s\S]*?\n    }\r?\n\r?\n    private void CloseTabCore/)?.[0] ?? "";
  assert.match(bulkTmux, /OptionText = persistent\.Count == 1/);
  assert.match(bulkTmux, /AcceptsY = true/);
  assert.match(bulkTmux, /dialog\.IsOptionChecked[\s\S]*?TryEndRemoteSessionAsync\(\)/);
  assert.match(bulkTmux, /tabs\.Where\(tab => !failed\.Contains\(tab\)\)[\s\S]*?CloseTabCore\(tab\)/);
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
