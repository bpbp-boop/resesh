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
    /private void AppWindow_Closing[\s\S]*?\n    }\r?\n\r?\n    private bool NeedsCloseConfirmation/)?.[0] ?? "";

  assert.match(handler, /if \(_closeConfirmed \|\| !ViewModel\.AllTabs\.Any\(\)\)/);
  assert.match(handler, /args\.Cancel = true/);
  // A repeated close request while the prompt waits brings the window forward rather than
  // looking dead.
  assert.match(handler, /args\.Cancel = true;[\s\S]*?BringToFront\(\);\s*if \(_closePromptOpen\)\s*return;/);
  assert.match(handler, /ConfirmWindowCloseAsync\(\)/);
});

test("the shared close dialog confirms exit unless no sessions remain", () => {
  const method = source.match(
    /private async Task ConfirmWindowCloseAsync[\s\S]*?\n    }\r?\n\r?\n    private void PinButton_Click/)?.[0] ?? "";

  // The count is taken after the dialogs queued ahead of the prompt are answered.
  assert.match(method, /WaitForQueuedDialogsAsync\(xamlRoot\);\s*if \(_windowClosed\)\s*return;\s*if \(!NeedsCloseConfirmation\(\)\)\s*\{\s*_closeConfirmed = true;\s*Close\(\);\s*return;[\s\S]*?var count = sessions\.Count;/);
  // A session that arrived while the prompt was open was not part of the question.
  assert.match(method, /if \(!confirmed \|\| _windowClosed\)\s*return;\s*if \(ViewModel\.AllTabs\.Any\([^;]*!sessions\.Contains\(tab\)\)\)\s*continue;/);
  assert.match(method, /catch \(Exception ex\)\s*\{\s*App\.ReportRecoverableError\(ex\);\s*\}\s*finally\s*\{\s*_closePromptOpen = false;/);
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
    /private async Task RequestCloseTmuxTabAsync[\s\S]*?\n    }\r?\n\r?\n(?:    \/\/\/.*\r?\n)*    public async Task RequestCloseManyAsync/)?.[0] ?? "";
  assert.match(tmuxClose, /AcceptsY = true/);

  const bulkClose = source.match(
    /public async Task RequestCloseManyAsync[\s\S]*?\n    }\r?\n\r?\n(?:    \/\/\/.*\r?\n)*    private void CloseTabCore/)?.[0] ?? "";
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
    /private async Task RequestCloseManyWithTmuxAsync[\s\S]*?\n    }\r?\n\r?\n(?:    \/\/\/.*\r?\n)*    private void CloseTabCore/)?.[0] ?? "";
  assert.match(bulkTmux, /OptionText = persistent\.Count == 1/);
  assert.match(bulkTmux, /AcceptsY = true/);
  assert.match(bulkTmux, /dialog\.IsOptionChecked[\s\S]*?TryEndRemoteSessionAsync\(\)/);
  assert.match(bulkTmux, /tabs\.Where\(tab => !failed\.Contains\(tab\)\)[\s\S]*?CloseTabCore\(tab\)/);
});

test("closing a tab hands off to its successor before removing the closing view", () => {
  const closeCore = source.match(
    /private void CloseTabCore[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(closeCore,
    /groupView\.CloseTerminal\(\s*tab\.View as UIElement,\s*\(\) => ViewModel\.DetachTab\(tab\),\s*\(\) => \(tab\.View as IDisposable\)\?\.Dispose\(\)\);/);
  assert.doesNotMatch(closeCore, /RemoveTerminal|ViewModel\.CloseTab/);
  // A late close of a tab that already left the window does nothing rather than throw.
  assert.match(closeCore, /ViewModel\.TryGroupOf\(tab\) is not \{ \} group[\s\S]*?return;/);
  assert.doesNotMatch(closeCore, /ViewModel\.GroupOf\(/);

  const groupView = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml.cs"), "utf8");
  const closeTerminal = groupView.match(
    /public void CloseTerminal[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  // Selection settles before any terminal changes visibility.
  assert.match(closeTerminal,
    /_terminalVisibilityDeferred = true;[\s\S]*?detachTab\(\);[\s\S]*?_terminalVisibilityDeferred = false;[\s\S]*?SyncTerminalVisibility\(\);/);
  // The closing view leaves the tree only after selection settles, then is disposed.
  assert.match(closeTerminal, /SyncTerminalVisibility\(\);\s*if \(view is not null\)\s*TerminalHost\.Children\.Remove\(view\);\s*disposeTab\(\);/);
});


// Closes wait on dialogs and remote shells; the tab may close, move to another window, or the
// window may close meanwhile. Each flow claims its tabs, re-checks after every await, and
// reports failures because its callers fire and forget.
test("tab closes re-check the tab after every await and claim it while in flight", () => {
  const single = source.match(
    /public async Task RequestCloseTabAsync[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(single, /ViewModel\.TryBeginClose\(tab\)/);
  assert.match(single, /catch \(Exception ex\)[\s\S]*?App\.ReportRecoverableError\(ex\)/);
  assert.match(single, /finally[\s\S]*?ViewModel\.EndClose\(tab\)/);

  const many = source.match(
    /public async Task RequestCloseManyAsync[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(many, /Where\(ViewModel\.TryBeginClose\)/);
  assert.match(many, /finally[\s\S]*?ViewModel\.EndClose\(tab\)/);

  const flows = source.match(
    /private async Task RequestCloseClaimedTabAsync[\s\S]*?\n    private void CloseTabCore/)?.[0] ?? "";
  // Every await of a dialog or remote shell is followed by a check before closing.
  const awaits = [...flows.matchAll(/await [^;]*?(?:ConfirmAsync|TryEndRemoteSessionAsync)\(/g)];
  assert.ok(awaits.length >= 5);
  for (const { 0: awaited, index } of awaits) {
    const after = flows.slice(index + awaited.length);
    const nextClose = after.search(/CloseTabCore\(tab\)|TogglePin\(tab\)/);
    const nextCheck = after.search(/CanStillClose\(tab\)|CloseOwnerOf\(tab\)|_windowClosed/);
    if (nextClose >= 0)
      assert.ok(nextCheck >= 0 && nextCheck < nextClose, `no re-check after: ${awaited}`);
  }
});

// A single-tab close answers a question about that tab, so if the tab was dragged to another
// window while the dialog was open, the window that holds it now closes it. Bulk closes are
// about this window's tabs and leave a tab that moved away alone.
test("a confirmed single-tab close follows the tab to the window that holds it now", () => {
  const owner = source.match(/private MainWindow\? CloseOwnerOf\(TabViewModel tab\) =>[\s\S]*?;\r?\n/)?.[0] ?? "";
  assert.match(owner, /CanStillClose\(tab\) \? this\s*: App\.WindowFor\(tab\) is \{ _windowClosed: false \} other \? other\s*: null/);

  const single = source.match(
    /private async Task RequestCloseClaimedTabAsync[\s\S]*?\n    \/\/\/ <summary>Bulk close/)?.[0] ?? "";
  // After the first await, only the current owner closes or unpins the tab.
  const afterFirstAwait = single.slice(single.indexOf("await "));
  assert.doesNotMatch(afterFirstAwait, /(?<![.\w])(?:CloseTabCore|TogglePin)\(tab\)/);
  assert.match(single, /pinnedOwner\.TogglePin\(tab\);\s*pinnedOwner\.CloseTabCore\(tab\);/);
  assert.match(single, /CloseOwnerOf\(tab\) is \{ \} owner\)\s*owner\.CloseTabCore\(tab\);/);
  assert.match(single, /owner\.TogglePin\(tab\);\s*owner\.CloseTabCore\(tab\);/);

  const bulk = source.match(
    /private async Task RequestCloseClaimedManyAsync[\s\S]*?\n    \/\/\/ <summary>Closes the tab now/)?.[0] ?? "";
  assert.doesNotMatch(bulk, /CloseOwnerOf/);
});
