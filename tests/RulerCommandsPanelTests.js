const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const source = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "wwwroot", "addon-ruler.js"),
  "utf8");
const pageSource = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "wwwroot", "terminal.html"),
  "utf8");

// Flash/copy-feedback/hide-grace paths run through setTimeout; a hand-drained queue
// keeps the tests deterministic. The clipboard stub records what a copy would write.
const pendingTimers = [];
const copied = [];
const window = { devicePixelRatio: 1 };
vm.runInNewContext(source, {
  window, Map, Math, RegExp, Set,
  requestAnimationFrame() {},
  setTimeout(fn) { pendingTimers.push(fn); return pendingTimers.length; },
  clearTimeout() {},
  navigator: { clipboard: { writeText(text) { copied.push(text); return { catch() {} }; } } },
});
const RulerAddon = window.RulerAddon.RulerAddon;
const runPendingTimers = () => pendingTimers.splice(0).forEach(fn => fn());

const line = (text, wrapped) => ({ isWrapped: !!wrapped, translateToString: () => text });
const buffer = (lines, opts) => ({
  type: (opts && opts.type) || "normal",
  baseY: (opts && opts.baseY) || 0,
  cursorY: (opts && opts.cursorY) || 0,
  cursorX: 0,
  get length() { return lines.length; },
  getLine: r => lines[r],
});

function makeAddon(active, normal) {
  const addon = new RulerAddon();
  const scrolled = [];
  let focused = 0;
  addon._term = {
    rows: 20,
    buffer: { active, normal: normal || active },
    registerMarker(offset) {
      const at = active.baseY + active.cursorY + (offset || 0);
      return { line: at, isDisposed: false, dispose() { this.isDisposed = true; }, onDispose() {} };
    },
    registerDecoration() { return { onRender() {}, dispose() {} }; },
    scrollToLine(l) { scrolled.push(l); },
    focus() { focused++; },
  };
  addon.scrolled = scrolled;
  addon.focusCount = () => focused;
  addon._paintQueued = true; // no canvas here; keep _queuePaint inert
  return addon;
}

// The tooltip and panel builders only need createElement/createTextNode and
// element children bookkeeping; textContent assignment clears children as real
// DOM does, and addEventListener captures handlers for synthetic clicks.
function makeElement(ownerDocument) {
  const el = {
    ownerDocument,
    children: [],
    style: {},
    className: "",
    handlers: {},
    _text: "",
    appendChild(child) { this.children.push(child); return child; },
    addEventListener(type, fn) { this.handlers[type] = fn; },
  };
  Object.defineProperty(el, "textContent", {
    get() { return el._text; },
    set(value) { el._text = value; el.children.length = 0; },
  });
  return el;
}
function makeDoc() {
  const doc = {
    createElement() { return makeElement(doc); },
    createTextNode(text) { return { textContent: text }; },
  };
  return doc;
}
const click = { stopPropagation() {} };

test("OSC 133 marks store the command text for the panel", () => {
  const lines = [line("")];
  const active = buffer(lines);
  const addon = makeAddon(active);
  addon._onOsc133("A");
  active.cursorX = 7;
  addon._onOsc133("B");
  lines[0] = line("u@h:~$ htop -d 10");
  active.cursorY = 1;
  addon._onOsc133("C");
  addon._onOsc133("D;0");

  const commands = addon.getCommands();
  assert.equal(commands.length, 1);
  assert.deepEqual(
    [commands[0].line, commands[0].exit, commands[0].src, commands[0].text],
    [0, 0, "osc", "htop -d 10"]);
});

test("discovered marks store the typed command for the panel", () => {
  const addon = makeAddon(buffer([line("u@h:~$ htop")]));
  addon.notifyEnter(1);
  runPendingTimers();

  const commands = addon.getCommands();
  assert.equal(commands[0].text, "htop");
  assert.equal(commands[0].src, "guess");
});

test("getCommands sorts by line and re-parses when no text was stored", () => {
  const addon = makeAddon(buffer([line("sw1#show version"), line("➜ mystery run")]));
  addon._cmdMarks = [
    { marker: { line: 1, isDisposed: false }, exit: null, src: "osc", text: "" },
    { marker: { line: 0, isDisposed: false }, exit: 0, src: "osc", text: "" },
  ];

  const commands = addon.getCommands();
  // Array.from: getCommands' array comes from the vm realm; map would stay there
  // and deepEqual rejects the cross-realm Array prototype.
  assert.deepEqual(Array.from(commands, c => c.line), [0, 1]);
  assert.equal(commands[0].text, "show version");
  assert.equal(commands[1].text, "➜ mystery run"); // unknown prompt: the raw line
});

test("command output spans to the next mark, joins wraps, drops empty-Enter prompts", () => {
  const lines = [
    line("u@h:~$ cat notes.txt"),
    line("line one that wr"),
    line("aps", true),
    line(""),
    line("u@h:~$"),
    line("u@h:~$ pwd"),
    line("/home/u"),
    line("u@h:~$"),
  ];
  const addon = makeAddon(buffer(lines, { cursorY: 7 }));
  addon._cmdMarks = [
    { marker: { line: 0, isDisposed: false }, exit: 0, src: "guess", text: "cat notes.txt" },
    { marker: { line: 5, isDisposed: false }, exit: 0, src: "guess", text: "pwd" },
  ];

  assert.equal(addon.getCommandOutput(0), "u@h:~$ cat notes.txt\nline one that wraps");
  // The last command's output ends before the live idle prompt (the cursor's line).
  assert.equal(addon.getCommandOutput(5), "u@h:~$ pwd\n/home/u");
});

test("the copied header joins a soft-wrapped command line; no output means no copy", () => {
  const lines = [
    line("u@h:~$ tail -f /var/lo"),
    line("g/syslog", true), // the COMMAND line itself wraps
    line("Aug 21 syslog says hi"),
    line("u@h:~$ true"),
    line("u@h:~$ pwd"), // adjacent mark: `true` printed nothing
  ];
  const addon = makeAddon(buffer(lines, { cursorY: 4 }));
  addon._cmdMarks = [
    { marker: { line: 0, isDisposed: false }, exit: 0, src: "guess", text: "tail -f /var/log/syslog" },
    { marker: { line: 3, isDisposed: false }, exit: 0, src: "guess", text: "true" },
    { marker: { line: 4, isDisposed: false }, exit: null, src: "guess", text: "pwd" },
  ];

  assert.equal(addon.getCommandOutput(0),
    "u@h:~$ tail -f /var/log/syslog\nAug 21 syslog says hi");
  // "" keeps the buttons' "No output" feedback instead of copying a lone header.
  assert.equal(addon.getCommandOutput(3), "");
});

test("prompt-SHAPED output lines survive the copy; only prompt-EQUAL lines drop", () => {
  const lines = [
    line("$ cat page.html"),
    line("<div>"),
    line("</html>"), // parses as a bare prompt shape but is real output
    line("$"),       // empty-Enter prompt, equal to the mark's own prompt
    line("$ pwd"),
    line("/home/u"),
    line("$"),
  ];
  const addon = makeAddon(buffer(lines, { cursorY: 6 }));
  addon._cmdMarks = [
    { marker: { line: 0, isDisposed: false }, exit: 0, src: "guess", text: "cat page.html" },
    { marker: { line: 4, isDisposed: false }, exit: 0, src: "guess", text: "pwd" },
  ];

  assert.equal(addon.getCommandOutput(0), "$ cat page.html\n<div>\n</html>");
});

test("the command popover offers Jump to and Copy output on the nearest mark", () => {
  const lines = [];
  for (let i = 0; i < 100; i++) lines.push(line("output " + i));
  lines[10] = line("u@h:~$ deploy");
  lines[18] = line("u@h:~$ status");
  const addon = makeAddon(buffer(lines, { cursorY: 99 }));
  const doc = makeDoc();
  addon._strip = { clientHeight: 100 };
  addon._tooltip = makeElement(doc);
  addon._tooltip.offsetHeight = 20;
  addon._cmdMarks = [
    { marker: { line: 10, isDisposed: false }, exit: 0, src: "osc", text: "deploy" },
    { marker: { line: 18, isDisposed: false }, exit: 1, src: "osc", text: "status" },
  ];

  addon._showTooltip(12); // both marks are in the hover region; 10 is nearer

  assert.equal(addon._tooltipCommand.marker.line, 10);
  assert.equal(addon._tooltip.style.pointerEvents, "auto");
  const actionRow = addon._tooltip.children[addon._tooltip.children.length - 1];
  assert.equal(actionRow.className, "srt-actions");
  assert.deepEqual(actionRow.children.map(b => b.textContent), ["Jump to", "Copy output"]);

  actionRow.children[0].handlers.click(click);
  assert.deepEqual(addon.scrolled, [0]); // line 10 centered in a 20-row viewport
  assert.equal(addon.focusCount(), 1);
  assert.equal(addon._tooltip.style.display, "none"); // Jump dismisses the popover

  addon._showTooltip(12);
  copied.length = 0;
  addon.onCopyText = text => copied.push(text);
  const copyButton = addon._tooltip.children[addon._tooltip.children.length - 1].children[1];
  copyButton.handlers.click(click);
  assert.equal(copied.length, 1);
  assert.ok(copied[0].startsWith("u@h:~$ deploy\noutput 11")); // command leads the copy
  assert.ok(copied[0].endsWith("output 17")); // stops before the next command mark
  assert.equal(copyButton.textContent, "Copied");
  runPendingTimers();
  assert.equal(copyButton.textContent, "Copy output");
});

test("a region without commands renders a passive tooltip", () => {
  const lines = [];
  for (let i = 0; i < 100; i++) lines.push(line("x"));
  const addon = makeAddon(buffer(lines, { cursorY: 99 }));
  const doc = makeDoc();
  addon._strip = { clientHeight: 100 };
  addon._tooltip = makeElement(doc);
  addon._tooltip.offsetHeight = 20;
  addon._cmdMarks = [
    { marker: { line: 90, isDisposed: false }, exit: 0, src: "osc", text: "deploy" },
  ];

  addon._showTooltip(12);

  assert.equal(addon._tooltipCommand, null);
  assert.equal(addon._tooltip.style.pointerEvents, "none");
  assert.ok(addon._tooltip.children.every(child => child.className !== "srt-actions"));
});

test("the commands panel lists marks with status dots and copies output", () => {
  const lines = [
    line("$ ok-cmd"), line("fine"),
    line("$ bad-cmd"), line("boom"),
    line("$ run-cmd"), line("..."),
    line("$"),
  ];
  const addon = makeAddon(buffer(lines, { cursorY: 6 }));
  const doc = makeDoc();
  addon._cmdPanelList = Object.assign(makeElement(doc), { scrollTop: 0, scrollHeight: 0, clientHeight: 0 });
  addon._cmdPanelCount = makeElement(doc);
  addon._cmdPanelOpen = true;
  addon._cmdMarks = [
    { marker: { line: 0, isDisposed: false }, exit: 0, src: "guess", text: "ok-cmd" },
    { marker: { line: 2, isDisposed: false }, exit: 1, src: "guess", text: "bad-cmd" },
    { marker: { line: 4, isDisposed: false }, exit: null, src: "guess", text: "run-cmd" },
  ];

  addon._refreshCommandsPanel();

  assert.equal(addon._cmdPanelCount.textContent, "3");
  const rows = addon._cmdPanelList.children;
  assert.equal(rows.length, 3);
  assert.deepEqual(rows.map(r => r.children[1].textContent), ["ok-cmd", "bad-cmd", "run-cmd"]);
  assert.deepEqual(rows.map(r => r.children[0].style.background),
    [addon._colors.cmdOk, addon._colors.cmdFail, addon._colors.cmdUnknown]);

  rows[1].handlers.click();
  assert.deepEqual(addon.scrolled, [0]);
  assert.equal(addon.focusCount(), 1);

  copied.length = 0;
  rows[0].children[3].handlers.click(click);
  assert.deepEqual(copied, ["$ ok-cmd\nfine"]);
  assert.equal(rows[0].children[3].textContent, "✓");
});

test("an empty panel says so instead of staying blank", () => {
  const addon = makeAddon(buffer([line("")]));
  const doc = makeDoc();
  addon._cmdPanelList = Object.assign(makeElement(doc), { scrollTop: 0, scrollHeight: 0, clientHeight: 0 });
  addon._cmdPanelCount = makeElement(doc);

  addon._refreshCommandsPanel();

  assert.equal(addon._cmdPanelList.children.length, 1);
  assert.equal(addon._cmdPanelList.children[0].className, "srp-empty");
  assert.equal(addon._cmdPanelCount.textContent, "");
});

test("toggleCommandsPanel flips state, honors a forced boolean, opens at the end", () => {
  const addon = makeAddon(buffer([line("")]));
  const doc = makeDoc();
  addon._cmdPanel = { style: {} };
  addon._cmdPanelList = Object.assign(makeElement(doc), { scrollTop: 0, scrollHeight: 40, clientHeight: 10 });
  addon._cmdPanelCount = makeElement(doc);
  const reported = [];
  addon.onCommandsPanelChanged = open => reported.push(open);

  addon.toggleCommandsPanel();
  assert.equal(addon._cmdPanelOpen, true);
  assert.equal(addon._cmdPanel.style.display, "flex");
  assert.equal(addon._cmdPanelList.scrollTop, 40); // pinned to the latest command

  addon.toggleCommandsPanel(false);
  assert.equal(addon._cmdPanelOpen, false);
  assert.equal(addon._cmdPanel.style.display, "none");
  // Every change reports out, so the host's native toggle button stays truthful.
  assert.deepEqual(reported, [true, false]);
});

test("the alternate buffer hides the panel along with the strip", () => {
  const context = { fillStyle: "", globalAlpha: 1, clearRect() {}, fillRect() {} };
  const addon = new RulerAddon();
  addon._term = { rows: 20, buffer: { active: { type: "alternate", length: 100, viewportY: 0 } } };
  addon._strip = { clientHeight: 100, style: {}, dataset: {} };
  addon._canvas = { width: 0, height: 0, getContext: () => context };
  addon._thumb = { style: {} };
  addon._cmdPanel = { style: {} };
  addon._cmdPanelOpen = true;

  addon._paint();
  assert.equal(addon._strip.style.display, "none");
  assert.equal(addon._cmdPanel.style.display, "none");

  addon._term.buffer.active.type = "normal";
  addon._paint();
  assert.equal(addon._cmdPanel.style.display, "flex");
});

test("the addon ships the themed panel chrome and no page-side toggle button", () => {
  assert.match(source, /scroll-ruler-panel/);
  assert.match(source, /--sr-bg/);
  assert.match(source, /srt-actions/);
  assert.match(source, /pointer-events:none/);
  assert.doesNotMatch(source, /scroll-ruler-toggle/); // the button is native WinUI now
});

test("the page wires Ctrl+Shift+O, the host toggle message, and the find bar dodge", () => {
  assert.match(pageSource, /e\.code === "KeyO"/);
  assert.match(pageSource, /case "toggleCommands":[\s\S]*?ruler\.toggleCommandsPanel\(\)/);
  assert.match(pageSource, /onCommandsPanelChanged[\s\S]*?type: "commandsPanel"/);
  assert.match(pageSource, /onCopyText[\s\S]*?type: "copy"/);
  assert.match(pageSource, /body\.find-open \.scroll-ruler-panel \{ top: 38px; \}/);
  assert.match(pageSource, /classList\.add\("find-open"\)/);
  assert.match(pageSource, /classList\.remove\("find-open"\)/);
});

test("the native Show commands button reaches the page panel through the tab view", () => {
  const control = fs.readFileSync(
    path.join(__dirname, "..", "src", "Terminal", "TerminalControl.cs"), "utf8");
  const tabView = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"), "utf8");
  const groupXaml = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml"), "utf8");
  const groupCode = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml.cs"), "utf8");

  assert.match(control, /public void ToggleCommandsPanel\(\) => Post\(new \{ type = "toggleCommands" \}\)/);
  assert.match(tabView, /public void ToggleCommandsPanel\(\)[\s\S]*?!_tab\.IsLocked[\s\S]*?_terminal\.ToggleCommandsPanel\(\)/);
  assert.match(groupXaml, /<ToggleButton[\s\S]{0,400}?x:Name="ShowCommandsButton"[\s\S]*?Click="ShowCommandsButton_Click"/);
  assert.match(groupXaml, /x:Name="ShowCommandsButton"[\s\S]*?Glyph="&#xE756;"/);
  assert.match(groupCode, /ShowCommandsButton_Click[\s\S]*?ToggleCommandsPanel\(\)/);
  // The whole action cluster hides when the group has no tabs.
  assert.match(groupCode, /TabStripActions\.Visibility = Group\.Tabs\.Count > 0/);
  assert.match(groupCode, /ShowCommandsButton\.IsEnabled = tab is not null && !tab\.IsLocked/);
});

test("an active toggle keeps flat chrome and shows an accent icon", () => {
  const control = fs.readFileSync(
    path.join(__dirname, "..", "src", "Terminal", "TerminalControl.cs"), "utf8");
  const tabView = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"), "utf8");
  const groupXaml = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml"), "utf8");
  const groupCode = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml.cs"), "utf8");

  // Page state -> control event -> tab view property -> IsChecked on the button.
  assert.match(control, /case "commandsPanel":[\s\S]*?CommandsPanelOpenChanged\?\.Invoke/);
  assert.match(tabView, /_terminal\.CommandsPanelOpenChanged \+= open =>[\s\S]*?IsCommandsPanelOpen = open/);
  assert.match(groupCode, /CommandsPanelOpenChanged \+= ActionButtonView_StateChanged/);
  assert.match(groupCode, /ShowCommandsButton\.IsChecked = commandsOpen/);

  // Resting buttons and checked toggles are flat. Hover and press reveal the
  // theme-specific surface, while checked state recolors the glyph.
  const actions = groupXaml.match(/x:Name="TabStripActions"[\s\S]*?<\/Grid>\r?\n\r?\n        <Grid x:Name="TerminalHost"/)?.[0]
    ?? groupXaml.match(/x:Name="TabStripActions"[\s\S]*$/)?.[0] ?? "";
  assert.match(actions, /x:Key="ToggleButtonForegroundChecked" ResourceKey="AccentTextFillColorPrimaryBrush"/);
  assert.match(actions, /x:Key="ToggleButtonBackgroundChecked" ResourceKey="TabActionButtonRestBrush"/);
  assert.match(actions, /x:Key="ToggleButtonBackgroundCheckedPointerOver" ResourceKey="TabActionButtonHoverBrush"/);
  assert.match(actions, /<Style TargetType="Button"[\s\S]*?BorderThickness" Value="0"/);
  assert.match(actions, /<Style TargetType="ToggleButton"[\s\S]*?BorderThickness" Value="0"/);
  assert.match(actions, /x:Key="ButtonForegroundPressed" ResourceKey="AccentTextFillColorPrimaryBrush"/);
  // Both theme dictionaries carry the aliases so a runtime theme swap re-resolves them.
  assert.equal((actions.match(/x:Key="ToggleButtonForegroundChecked"/g) || []).length, 2);
});
