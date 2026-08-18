const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const source = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "wwwroot", "addon-ruler.js"),
  "utf8");

// The command-title paths run through setTimeout (echo-settle probe); a queue the
// tests drain by hand keeps them deterministic instead of clock-dependent.
const pendingTimers = [];
const window = { devicePixelRatio: 1 };
vm.runInNewContext(source, {
  window, Map, Math, RegExp, Set,
  requestAnimationFrame() {},
  setTimeout(fn) { pendingTimers.push(fn); return pendingTimers.length; },
  clearTimeout() {},
});
const RulerAddon = window.RulerAddon.RulerAddon;
const runPendingTimers = () => pendingTimers.splice(0).forEach(fn => fn());

const line = (text, wrapped) => ({ isWrapped: !!wrapped, translateToString: () => text });
const buffer = (lines, type) =>
  ({ type: type || "normal", baseY: 0, cursorY: 0, cursorX: 0, getLine: r => lines[r] });

function makeAddon(active, normal) {
  const addon = new RulerAddon();
  let markers = 0;
  addon._term = {
    buffer: { active, normal: normal || active },
    registerMarker() {
      markers++;
      return { line: 0, isDisposed: false, dispose() { this.isDisposed = true; }, onDispose() {} };
    },
  };
  addon.markerCount = () => markers;
  addon._paintQueued = true; // no canvas here; keep _queuePaint inert
  addon.calls = [];
  addon.onRunningCommand = (text, epoch) => addon.calls.push([text, epoch]);
  return addon;
}

test("_cmdText slices from the 133;B column and follows soft wraps", () => {
  const addon = makeAddon(buffer([
    line("u@h:~$ tail -f /var/lo"),
    line("g/syslog", true),
    line("out", false),
  ]));
  assert.equal(addon._cmdText(addon._term.buffer.active, 0, 7), "tail -f /var/log/syslog");
});

test("_cmdText falls back to the prompt regex when the column is unknown", () => {
  const addon = makeAddon(buffer([line("sw1#show version")]));
  assert.equal(addon._cmdText(addon._term.buffer.active, 0, -1), "show version");
});

test("OSC 133 B remembers the input start, C reports the text, D reports the end", () => {
  const lines = [line("")];
  const active = buffer(lines);
  const addon = makeAddon(active);
  addon._onOsc133("A");
  active.cursorX = 7; // prompt painted, cursor at the input start
  addon._onOsc133("B");
  lines[0] = line("u@h:~$ htop -d 10");
  active.cursorY = 1; // Enter echoed
  addon._onOsc133("C");
  addon._onOsc133("D;0");
  assert.deepEqual(addon.calls, [["htop -d 10", undefined], ["", undefined]]);
});

test("discovery reports the command with the page's epoch and commits a mark", () => {
  const addon = makeAddon(buffer([line("u@h:~$ htop")]));
  addon.notifyEnter(42);
  runPendingTimers(); // echo-settle probe
  assert.deepEqual(addon.calls, [["htop", 42]]);
  assert.equal(addon.markerCount(), 2); // the probe plus the committed mark
});

test("discovery still titles a full-screen app that took the alternate screen, without a mark", () => {
  const normal = buffer([line("u@h:~$ vim notes.txt")]);
  const active = buffer([line("~")], "normal"); // normal at Enter time...
  const addon = makeAddon(active, normal);
  addon.notifyEnter(7);
  active.type = "alternate"; // ...the app flips the screen before the probe fires
  runPendingTimers(); // settle attempt: title fires, mark withheld, retry queued
  runPendingTimers(); // retry: still alternate, probe gives up
  assert.deepEqual(addon.calls, [["vim notes.txt", 7]]);
  assert.equal(addon.markerCount(), 1); // the probe only — no mark on the alternate screen
});

test("discovery stays quiet on a line without a prompt shape", () => {
  const addon = makeAddon(buffer([line("Password:")]));
  addon.notifyEnter(1);
  runPendingTimers();
  runPendingTimers();
  assert.equal(addon.calls.length, 0);
});

test("idle Windows prompts report their current directory", () => {
  const cmd = makeAddon(buffer([line("C:\\Users\\Boden>")]));
  cmd.promptCalls = [];
  cmd.onPromptContext = (text, platform) => cmd.promptCalls.push([text, platform]);
  cmd._reportPromptContext();

  const powershell = makeAddon(buffer([line("PS D:\\work\\Sessions> ")]));
  powershell.promptCalls = [];
  powershell.onPromptContext = (text, platform) => powershell.promptCalls.push([text, platform]);
  powershell._reportPromptContext();

  assert.deepEqual(cmd.promptCalls, [["C:\\Users\\Boden", null]]);
  assert.deepEqual(powershell.promptCalls, [["D:\\work\\Sessions", null]]);
});

test("prompt reporting rejects output and reports the same directory on a new line", () => {
  const lines = [line("build > artifact.txt")];
  const active = buffer(lines);
  const addon = makeAddon(active);
  addon.promptCalls = [];
  addon.onPromptContext = text => addon.promptCalls.push(text);
  addon._reportPromptContext();
  assert.deepEqual(addon.promptCalls, []);

  lines[0] = line("C:\\work>");
  lines[1] = line("C:\\work>");
  addon._reportPromptContext();
  active.cursorY = 1;
  addon._reportPromptContext();
  assert.deepEqual(addon.promptCalls, ["C:\\work", "C:\\work"]);
});

test("Nokia MD-CLI prompts report operational and candidate contexts", () => {
  const lines = [line("[/show]"), line("A:boden@bng-sr01#")];
  const active = buffer(lines);
  active.cursorY = 1;
  const addon = makeAddon(active);
  addon.promptCalls = [];
  addon.onPromptContext = (text, platform) => addon.promptCalls.push([text, platform]);

  addon._reportPromptContext();
  lines[0] = line('*[pr:/configure router "Base"]');
  addon._reportPromptContext();

  assert.deepEqual(addon.promptCalls, [
    ["/show", "nokia"],
    ['private* \u00b7 /configure router "Base"', "nokia"],
  ]);
});

test("a bracketed line without the Nokia identity prompt is not a device context", () => {
  const active = buffer([line("[/show]"), line("ordinary output")]);
  active.cursorY = 1;
  const addon = makeAddon(active);
  addon.promptCalls = [];
  addon.onPromptContext = text => addon.promptCalls.push(text);

  addon._reportPromptContext();

  assert.deepEqual(addon.promptCalls, []);
});

test("Junos login and edit prompts report mode, hierarchy, and routing-engine role", () => {
  const lines = [line("--- JUNOS 24.4R1.9 built 2025-01-10"), line("boden@mx-1>")];
  const active = buffer(lines);
  active.cursorY = 1;
  const addon = makeAddon(active);
  addon.promptCalls = [];
  addon.onPromptContext = (text, platform) => addon.promptCalls.push([text, platform]);

  addon._reportPromptContext();
  lines[0] = line("{master:0}[edit protocols bgp group core]");
  lines[1] = line("boden@mx-1#");
  addon._reportPromptContext();
  lines[0] = line("{master:0}");
  lines[1] = line("boden@mx-1>");
  addon._reportPromptContext();

  assert.deepEqual(addon.promptCalls, [
    ["operational", "juniper"],
    ["master:0 \u00b7 configure \u00b7 /protocols bgp group core", "juniper"],
    ["master:0 \u00b7 operational", "juniper"],
  ]);
});

test("a user-at-host operational prompt alone does not claim to be Junos", () => {
  const active = buffer([line("admin@PA-VM>")]);
  const addon = makeAddon(active);
  addon.promptCalls = [];
  addon.onPromptContext = (text, platform) => addon.promptCalls.push([text, platform]);

  addon._reportPromptContext();

  assert.deepEqual(addon.promptCalls, []);
});
