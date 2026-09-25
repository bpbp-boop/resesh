const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const root = path.join(__dirname, "..");
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), "utf8");

const table = read("src", "Core", "Input", "KeyBindings.cs");
const page = read("src", "Terminal", "wwwroot", "terminal.html");
const control = read("src", "Terminal", "TerminalControl.cs");
const native = read("src", "Terminal", "NativeTerminalSurface.cs");
const mainWindow = read("src", "App", "MainWindow.xaml.cs");

const idValues = Object.fromEntries(
  [...table.matchAll(/public const string (\w+) = "([^"]+)";/g)].map(m => [m[1], m[2]]));
const bindings = [...table.matchAll(/Add\(ShortcutIds\.(\w+),\s*\w+,\s*"[^"]*",\s*ShortcutScope\.(\w+)/g)]
  .map(m => ({ name: m[1], id: idValues[m[1]], scope: m[2] }));

function methodBody(source, signature) {
  const start = source.indexOf(signature);
  assert.ok(start >= 0, `missing ${signature}`);
  const open = source.indexOf("{", start);
  let depth = 0;
  for (let i = open; i < source.length; i++) {
    if (source[i] === "{") depth++;
    else if (source[i] === "}" && --depth === 0) return source.slice(open, i + 1);
  }
  throw new Error(`unterminated ${signature}`);
}

test("the table parses into every scope", () => {
  assert.ok(bindings.length >= 40);
  for (const scope of ["App", "Window", "Terminal", "SessionTree"])
    assert.ok(bindings.some(b => b.scope === scope), scope);
  for (const binding of bindings) assert.ok(binding.id, binding.name);
});

test("the terminal page runs every terminal-scope shortcut", () => {
  const actions = page.slice(page.indexOf("const terminalActions = {"), page.indexOf("function runTerminalAction"));
  for (const binding of bindings.filter(b => b.scope === "Terminal"))
    assert.ok(actions.includes(`"${binding.id}"`), binding.id);
});

test("the page matches chords from the host table, not hardcoded keys", () => {
  assert.match(page, /const shortcuts = Array\.isArray\(init\.shortcuts\) \? init\.shortcuts : \[\];/);
  assert.match(page, /chord\.key === e\.keyCode && chord\.ctrl === e\.ctrlKey &&\s*chord\.shift === e\.shiftKey && chord\.alt === e\.altKey/);
  assert.doesNotMatch(page, /e\.code === "Key[A-Z]"/);
  assert.doesNotMatch(page, /type: "(closeTab|splitTab|filePane|newLocalTab|commandPalette|quickConnect)"/);
  assert.match(page, /host\.postMessage\(\{ type: "shortcut", id: shortcut\.id, chord: match\.chord \}\)/);
});

test("split-only shortcuts leave Alt+Arrow to the shell in an unsplit window", () => {
  assert.match(page, /if \(shortcut\.whenSplit && !isSplit\) return;/);
  assert.match(page, /isSplit = msg\.isSplit === true;/);
  assert.match(native, /if \(shortcut\.WhenSplit && !_isSplit\)\s*return false;/);
});

test("the find field keeps its text-editing keys", () => {
  assert.match(page, /if \(inTextField && shortcut\.id !== "terminal\.find"\) return;/);
});

test("Shift+Enter still sends ESC CR on the normal buffer", () => {
  assert.ok(page.includes('term.input("\\x1b\\r", true);'));
});

test("WebView2 hands the table to the page and raises forwarded shortcuts", () => {
  assert.match(control, /shortcuts = Shortcuts\.Select\(/);
  assert.match(control, /case "shortcut":/);
  assert.match(control, /ShortcutRequested\?\.Invoke\(id, chord\);/);
  assert.match(control, /type = "invokeShortcut"/);
});

test("the native surface matches the same table and never strands suppressed input", () => {
  const handler = methodBody(native, "private bool TryHandleAppShortcut(ushort virtualKey)");
  assert.match(handler, /MatchShortcut\(virtualKey, control, shift, alt\)/);
  assert.match(native, /_suppressCharactersForKey != 0 && _suppressCharactersForKey != virtualKey/);
  const lostFocus = methodBody(native, "private void OnTerminalLostFocus");
  assert.match(lostFocus, /_suppressCharactersForKey = 0;/);
  const run = methodBody(native, "private bool RunTerminalShortcut(string id)");
  for (const [, id] of run.matchAll(/"(terminal\.\w+)"/g))
    assert.ok(bindings.some(b => b.id === id && b.scope === "Terminal"), id);
});

test("the window runs every app and window shortcut", () => {
  const body = methodBody(mainWindow, "private bool ExecuteShortcut(string id, int chord, TabViewModel? source = null)");
  for (const binding of bindings.filter(b => b.scope === "App" || b.scope === "Window"))
    assert.ok(body.includes(`ShortcutIds.${binding.name}`), binding.name);
});

test("window accelerators are registered from the table only", () => {
  const register = methodBody(mainWindow, "private void RegisterAccelerators()");
  assert.match(register, /foreach \(var binding in KeyBindings\.All\)/);
  assert.doesNotMatch(mainWindow, /new KeyboardAccelerator \{ Key = VirtualKey\./);
});

test("the session tree reads Enter, F2 and Delete from the table", () => {
  const handler = methodBody(mainWindow, "private void SessionTree_KeyDown(object sender, KeyRoutedEventArgs e)");
  assert.match(handler, /ShortcutScope\.SessionTree/);
  for (const binding of bindings.filter(b => b.scope === "SessionTree"))
    assert.ok(handler.includes(`ShortcutIds.${binding.name}`), binding.name);
});

test("menus and the palette take their labels from the table", () => {
  assert.doesNotMatch(read("src", "App", "MainWindow.xaml"), /KeyboardAcceleratorTextOverride="/);
  assert.doesNotMatch(read("src", "App", "Controls", "TabGroupView.xaml.cs"), /KeyboardAcceleratorTextOverride = "/);
  const palette = methodBody(mainWindow, "private IReadOnlyList<CommandPaletteEntry> BuildCommandPalette()");
  assert.doesNotMatch(palette, /"Ctrl\+/);
});
