const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const root = path.join(__dirname, "..");
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), "utf8");

const table = read("src", "Core", "Input", "KeyBindings.cs");
const surface = read("src", "Terminal", "TerminalSurface.cs");
const ghostty = read("src", "Terminal", "Native", "NativeTerminalSurface.cs");
const mainWindow = require("./support/mainWindowSource");

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

test("the terminal surface runs every terminal-scope shortcut", () => {
  const actions = methodBody(ghostty, "private bool RunTerminalShortcut(string id)");
  for (const binding of bindings.filter(b => b.scope === "Terminal"))
    assert.ok(actions.includes(`"${binding.id}"`), binding.id);
});

test("the terminal matches chords from the host table, not hardcoded keys", () => {
  assert.match(surface, /foreach \(var shortcut in _shortcuts\)/);
  assert.match(surface, /chord\.Key == virtualKey && chord\.Ctrl == control && chord\.Shift == shift && chord\.Alt == alt/);
  assert.match(ghostty, /if \(shortcut\.Forward\)\s*ShortcutRequested\?\.Invoke\(shortcut\.Id, chord\);/);
});

test("split-only shortcuts leave Alt+Arrow to the shell in an unsplit window", () => {
  assert.match(ghostty, /if \(shortcut\.WhenSplit && !_isSplit\)\s*return false;/);
});

test("Shift+Enter still sends ESC CR on the normal buffer", () => {
  assert.match(ghostty, /!_commandBuffer!\.IsAlternate/);
  assert.ok(ghostty.includes("SendUserInput([0x1B, 0x0D]);"));
});

test("the app hands the table to the terminals and runs forwarded shortcuts", () => {
  assert.match(read("src", "App", "App.xaml.cs"), /Resesh\.Terminal\.TerminalSurface\.Shortcuts = AppShortcuts\.ForTerminals\(\);/);
  assert.match(read("src", "App", "Terminal", "TerminalTabView.cs"), /_terminal\.ShortcutRequested \+= \(id, chord\) =>/);
});

test("the window runs every app and window shortcut", () => {
  // Layout keys are handled in ExecuteShortcut; every other key runs its catalog command.
  const body = methodBody(mainWindow, "private bool ExecuteShortcut(string id, int chord, TabViewModel? source = null)");
  assert.match(body, /ViewModel\.Commands\.ForShortcut\(id\)/);
  const catalog = read("src", "App", "ViewModels", "AppCommandCatalog.cs");
  for (const binding of bindings.filter(b => b.scope === "App" || b.scope === "Window"))
    assert.ok(body.includes(`ShortcutIds.${binding.name}`) || catalog.includes(`ShortcutIds.${binding.name}`),
      binding.name);
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
