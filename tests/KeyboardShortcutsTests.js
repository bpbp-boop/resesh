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
  for (const scope of ["App", "Window", "Terminal", "SessionTree", "CommandHistory", "HistoryCompare", "Welcome"])
    assert.ok(bindings.some(b => b.scope === scope), scope);
  for (const binding of bindings) assert.ok(binding.id, binding.name);
});

test("the terminal surface runs every terminal-scope shortcut", () => {
  const actions = methodBody(ghostty, "private bool RunTerminalShortcut(string id)");
  for (const binding of bindings.filter(b => b.scope === "Terminal"))
    assert.ok(actions.includes(`"${binding.id}"`), binding.id);
});

// The Terminal project cannot see ShortcutIds, so it spells the ids out. Each one it spells
// must be a terminal-scope id in the table, or a renamed id would silently stop working.
test("every shortcut id the terminal spells out is in the table", () => {
  const actions = methodBody(ghostty, "private bool RunTerminalShortcut(string id)");
  const cases = [...actions.matchAll(/"([a-z]+\.[A-Za-z]+)"/g)].map(m => m[1]);
  assert.ok(cases.length >= 10);
  const terminalIds = new Set(bindings.filter(b => b.scope === "Terminal").map(b => b.id));
  for (const id of cases) assert.ok(terminalIds.has(id), `RunTerminalShortcut handles unknown id ${id}`);

  const allIds = new Set(bindings.map(b => b.id));
  const prefixes = new Set(bindings.map(b => b.id.split(".")[0]));
  let named = 0;
  for (const file of sourceFiles(path.join("src", "Terminal"), ".cs")) {
    for (const [, id] of stripComments(fs.readFileSync(file, "utf8")).matchAll(/"([a-z]+\.[A-Za-z]+)"/g)) {
      if (!prefixes.has(id.split(".")[0])) continue;
      named++;
      assert.ok(allIds.has(id), `${path.relative(root, file)} names ${id}, which the table does not have`);
    }
  }
  assert.ok(named > cases.length, "the commands panel tooltip names its shortcut id too");
});

function sourceFiles(dir, ...extensions) {
  const found = [];
  for (const entry of fs.readdirSync(path.join(root, dir), { withFileTypes: true })) {
    if (entry.name === "bin" || entry.name === "obj") continue;
    const relative = path.join(dir, entry.name);
    if (entry.isDirectory()) found.push(...sourceFiles(relative, ...extensions));
    else if (extensions.some(extension => entry.name.endsWith(extension))) found.push(path.join(root, relative));
  }
  return found;
}

/** Blanks // comments and XAML comments, keeping line numbers. */
function stripComments(source) {
  return source
    .replace(/<!--[\s\S]*?-->/g, comment => comment.replace(/[^\n]/g, " "))
    .split(/\r?\n/)
    .map(line => line.replace(/^\s*\/\/.*$/, ""))
    .join("\n");
}

// Text such as "Next match (F3)" or "(Ctrl+Shift+E)" goes stale when a key changes. Labels
// come from AppShortcuts.Label or AppShortcuts.ToolTip, which read the table.
test("tooltips and labels never type a shortcut by hand", () => {
  const chordText = /\((?:[^()"]*\b(?:Ctrl|Shift|Alt)\+[^()"]*|F\d{1,2})\)/;
  // Keys a text field handles itself, not shortcuts: the terminal's find box steps with
  // Enter and Shift+Enter, and the Terminal project has no access to the table.
  const allowed = new Set([
    "src/Terminal/Native/NativeTerminalSurface.cs: Newer match, below (Shift+Enter)",
  ]);
  const found = [];
  const files = [
    ...sourceFiles(path.join("src", "App"), ".cs", ".xaml"),
    ...sourceFiles(path.join("src", "Terminal"), ".cs"),
  ];
  for (const file of files) {
    const lines = stripComments(fs.readFileSync(file, "utf8")).split("\n");
    lines.forEach((line, index) => {
      const strings = file.endsWith(".xaml")
        ? [...line.matchAll(/="([^"]*)"/g)].map(m => m[1])
        : [...line.matchAll(/"((?:[^"\\\n]|\\.)*)"/g)].map(m => m[1]);
      for (const text of strings.filter(text => chordText.test(text))) {
        const key = `${path.relative(root, file).replaceAll("\\", "/")}: ${text}`;
        if (!allowed.has(key)) found.push(`${key} (line ${index + 1})`);
      }
    });
  }
  assert.deepEqual(found, []);
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
  // Settings entries, and their shortcut, are rows of SettingsCatalog.
  const catalog = read("src", "App", "ViewModels", "AppCommandCatalog.cs") + read("src", "App", "ViewModels", "SettingsTargets.cs");
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

test("the sessions table in Settings gives Enter, F2 and Delete the tree's meaning", () => {
  const manager = read("src", "App", "Controls", "SessionsManagerView.xaml.cs");
  const handler = methodBody(manager, "private async void SessionList_KeyDown(object sender, KeyRoutedEventArgs e)");
  assert.match(handler, /KeyBindings\.Find\([^;]*ShortcutScope\.SessionTree\)/);
  for (const binding of bindings.filter(b => b.scope === "SessionTree"))
    assert.ok(handler.includes(`ShortcutIds.${binding.name}`), binding.name);
  assert.doesNotMatch(handler, /VirtualKey\./);
});

test("command history handles and hints every key of its scopes from the table", () => {
  const history = read("src", "App", "Controls", "HistoryView.xaml.cs");
  const handler = methodBody(history, "private void View_KeyDown(object sender, KeyRoutedEventArgs e)");
  assert.match(handler, /ShortcutScope\.HistoryCompare : ShortcutScope\.CommandHistory/);
  assert.doesNotMatch(handler, /VirtualKey\./);
  const hints = methodBody(history, "private void BuildKeyHints()");
  assert.doesNotMatch(hints, /Hint\("/);
  for (const binding of bindings.filter(b => b.scope === "CommandHistory" || b.scope === "HistoryCompare")) {
    assert.ok(handler.includes(`ShortcutIds.${binding.name}`), `${binding.name} is not handled`);
    // The hint bar is short: it leaves out paging, Shift+F3 and Delete, as it always has.
    if (!["HistorySelectPage", "HistoryPreviousMatch", "HistoryDelete"].includes(binding.name))
      assert.ok(hints.includes(`ShortcutIds.${binding.name}`), `${binding.name} has no hint`);
  }
});

test("Welcome's finish key comes from the table", () => {
  assert.doesNotMatch(read("src", "App", "Controls", "OnboardingView.xaml"), /<KeyboardAccelerator /);
  assert.match(read("src", "App", "Controls", "OnboardingView.xaml.cs"),
    /AppShortcuts\.AddAccelerators\(this, [\w.]*ShortcutIds\.FinishWelcome, FinishSetup_Invoked\)/);
});

test("the palette's terminal entries are the table's terminal palette bindings", () => {
  const body = methodBody(mainWindow, "private IReadOnlyList<CommandPaletteEntry> BuildTerminalPageCommands()");
  assert.match(body, /KeyBindings\.All\s*\.Where\(binding => binding\.InTerminalPalette\)/);
  assert.match(body, /Title = binding\.Title/);
  assert.doesNotMatch(body, /ShortcutIds\./);
});

test("menus and the palette take their labels from the table", () => {
  assert.doesNotMatch(read("src", "App", "MainWindow.xaml"), /KeyboardAcceleratorTextOverride="/);
  assert.doesNotMatch(read("src", "App", "Controls", "TabGroupView.xaml.cs"), /KeyboardAcceleratorTextOverride = "/);
  const palette = methodBody(mainWindow, "private IReadOnlyList<CommandPaletteEntry> BuildCommandPalette()");
  assert.doesNotMatch(palette, /"Ctrl\+/);
});
