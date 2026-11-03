const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

// Views that start background work keep only the newest result and drop work that finishes
// after they close. These checks pin the guards in place.
const root = path.join(__dirname, "..", "src", "App");
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), "utf8");

function methodBody(source, signature) {
  const start = source.indexOf(signature);
  assert.notEqual(start, -1, `missing ${signature}`);
  const open = source.indexOf("{", start);
  let depth = 0;
  for (let index = open; index < source.length; index++) {
    if (source[index] === "{")
      depth++;
    else if (source[index] === "}" && --depth === 0)
      return source.slice(open, index + 1);
  }
  throw new Error(`unbalanced ${signature}`);
}

test("history searches drop a waiting debounce and dispose their cancellation", () => {
  const history = read("Controls", "HistoryView.xaml.cs");
  const search = methodBody(history, "private async Task RunSearchAsync(");
  assert.match(search, /_searchDebounce\?\.Stop\(\)/);
  assert.match(search, /finally[\s\S]*cancellation\.Dispose\(\)/);
});

test("history loads apply only the newest result", () => {
  const history = read("Controls", "HistoryView.xaml.cs");
  const load = methodBody(history, "private async Task LoadAsync(");
  assert.match(load, /var version = \+\+_loadVersion;/);
  assert.match(load, /version != _loadVersion \|\| !IsOpen/);
  assert.match(methodBody(history, "public void Close()"), /_loadVersion\+\+/);
});

test("remote session management connections stop when the tab closes", () => {
  const view = read("Terminal", "TerminalTabView.cs");
  const create = methodBody(view, "public async Task<SshTerminalSession?> CreateRemoteManagementConnectionAsync(");
  assert.match(create, /CreateLinkedTokenSource\(_closing\.Token, cancellationToken\)/);
  assert.match(create, /token\.Register\(session\.Disconnect\)/);
  assert.match(create, /cancellationToken: token/);
  assert.doesNotMatch(create, /CancellationToken\.None/);
  assert.match(methodBody(view, "public void Dispose()"), /_closing\.Cancel\(\)/);
});

test("login prompts name a jump host's place on the route being dialled", () => {
  // A stopped tab's management connection resolves its own route; a shared "last route"
  // field would label its prompts from whatever the terminal dialled last.
  const view = read("Terminal", "TerminalTabView.cs");
  assert.doesNotMatch(view, /_routeSessions/);
  assert.match(view, /private static string\? RouteStep\(Session\? host, IReadOnlyList<Session> route\)/);
  const create = methodBody(view, "public async Task<SshTerminalSession?> CreateRemoteManagementConnectionAsync(");
  assert.match(create, /ResolveSshCredentialAsync\(Session, route\.Sessions, token\)/);
  const resolveRoute = methodBody(view, "private async Task<ResolvedRoute?> ResolveRouteAsync(");
  assert.match(resolveRoute, /ResolveSshCredentialAsync\(jump, route, token\)/);
});

test("the tunnels dialog leaves its UI alone once closed but still saves a confirmed add", () => {
  const dialog = read("Dialogs", "TunnelsDialog.xaml.cs");
  assert.match(methodBody(dialog, "private void Dialog_Closed("), /_closed = true;/);
  assert.match(methodBody(dialog, "private void Render()"), /if \(_closed\)\s+return;/);
  const add = methodBody(dialog, "private Task AddAsync(");
  const save = add.indexOf("var save = SaveBox.IsChecked == true;");
  assert.ok(save !== -1 && save < add.indexOf("await _view.StartTunnelAsync"), "Save choice is read before awaiting");
  assert.match(add, /if \(!_closed\)\s+TunnelFields\.ShowError/);
  assert.match(add, /if \(!_closed\)\s+TunnelFields\.Reset\(\)/);
  assert.match(add, /if \(save && _savedSession\(\)/);
});

test("only the newest font scan fills the font picker", () => {
  const picker = read("Controls", "FontFamilyPicker.xaml.cs");
  const refresh = methodBody(picker, "private async Task RefreshFontsAsync()");
  assert.match(refresh, /var version = \+\+_refreshVersion;/);
  assert.match(refresh, /version != _refreshVersion/);
});

test("the icon picker re-enables selection even if a refill throws", () => {
  const picker = read("Controls", "IconPicker.xaml.cs");
  assert.match(methodBody(picker, "private void ApplyFilter("), /_refilling = true;\s+try[\s\S]*finally[\s\S]*_refilling = false;/);
});

test("history closes only after an inserted command was accepted", () => {
  // A shell without bracketed paste asks before a multi-line insert; cancelling that must
  // leave the history open where the user was.
  const history = read("Controls", "HistoryView.xaml.cs");
  assert.match(history, /public Func<CommandHistoryEntry, Task<bool>>\? InsertCommand/);
  assert.match(methodBody(history, "private async Task InsertSelectedAsync("), /if \(await InsertCommand\(entry\) && IsOpen\)\s*CloseRequested\?\.Invoke\(\);/);
  const view = read("Terminal", "TerminalTabView.cs");
  assert.match(methodBody(view, "public async Task<bool> InsertTextAsync("), /await PasteForReviewAsync\(text\)/);
  const paste = read("Terminal", "TerminalTabView.Paste.cs");
  assert.match(methodBody(paste, "private async Task<bool> PasteForReviewAsync("), /return await paste;/);
  assert.match(methodBody(paste, "private async Task<bool> PasteAsync("), /if \(confirm && !await ConfirmPasteAsync\(text, insert\)\)[\s\S]*?return false;/);
});

test("the paste confirmation's button names the action its title asks about", () => {
  const xaml = read("Dialogs", "PasteConfirmDialog.xaml");
  assert.doesNotMatch(xaml, /PrimaryButtonText=/);
  assert.match(read("Dialogs", "PasteConfirmDialog.xaml.cs"), /PrimaryButtonText = verb;/);
  const confirm = methodBody(read("Terminal", "TerminalTabView.Paste.cs"), "private async Task<bool> ConfirmPasteAsync(");
  assert.match(confirm, /var verb = insert \? "Insert" : "Paste";[\s\S]*?new PasteConfirmDialog\(\s*[^,]+,\s*verb,/);
});
