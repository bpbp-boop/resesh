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

test("_cmdText recognizes a spaced Bash user, host, and cwd prompt", () => {
  const addon = makeAddon(buffer([line("u@h ~/work $ htop -d 10")]));
  assert.equal(addon._cmdText(addon._term.buffer.active, 0, -1), "htop -d 10");
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

test("OSC 3008 adds the stock systemd command result to a discovered mark", () => {
  const addon = makeAddon(buffer([line("u@h:~$ false")]));
  addon.notifyEnter(17);
  addon._onOsc3008("start=cmd-1;type=command;hostname=h;cwd=/home/u");
  addon._onOsc3008("end=cmd-1;exit=failure;status=1");
  runPendingTimers();

  assert.deepEqual(addon.calls, [["false", 17]]);
  assert.equal(addon._cmdMarks.length, 1);
  assert.equal(addon._cmdMarks[0].exit, 1);
  assert.equal(addon._cmdOscSeen, false); // 3008 complements discovery; it does not disable it.
});

test("OSC 3008 success without a status colors a discovered mark as exit zero", () => {
  const addon = makeAddon(buffer([line("u@h:~$ true")]));
  addon.notifyEnter(18);
  addon._onOsc3008("start=cmd-2;type=command");
  runPendingTimers();
  addon._onOsc3008("end=cmd-2;exit=success");

  assert.equal(addon._cmdMarks[0].exit, 0);
});

test("OSC 3008 accepts escaped context-ID separators", () => {
  const addon = makeAddon(buffer([line("u@h:~$ true")]));
  const parsed = addon._parseOsc3008("start=part\\x3bone\\x5ctwo;type=command");
  assert.equal(parsed.id, "part;one\\two");
});

test("OSC 3008 rejects an oversize payload instead of accepting its prefix", () => {
  const addon = makeAddon(buffer([line("u@h:~$ true")]));
  assert.equal(addon._parseOsc3008("start=id;type=command;x=" + "a".repeat(4096)), null);
});

test("discovery reports the command with the page's epoch and commits a mark", () => {
  const addon = makeAddon(buffer([line("u@h:~$ htop")]));
  addon.notifyEnter(42);
  runPendingTimers(); // echo-settle probe
  assert.deepEqual(addon.calls, [["htop", 42]]);
  assert.equal(addon.markerCount(), 2); // the probe plus the committed mark
});

test("discovery reports a command from a spaced Bash prompt", () => {
  const addon = makeAddon(buffer([line("u@h ~ $ less notes.txt")]));
  addon.notifyEnter(9);
  runPendingTimers();
  assert.deepEqual(addon.calls, [["less notes.txt", 9]]);
  assert.equal(addon.markerCount(), 2);
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

  cmd._reportPromptContext();
  assert.equal(cmd.promptCalls.length, 1);
  cmd._reportPromptContext(true);
  assert.deepEqual(cmd.promptCalls[1], ["C:\\Users\\Boden", null]);
});

test("an idle spaced Bash prompt reports its current directory", () => {
  const addon = makeAddon(buffer([line("u@h ~/work $ ")]));
  addon.promptCalls = [];
  addon.onPromptContext = (text, platform) => addon.promptCalls.push([text, platform]);

  addon._reportPromptContext();

  assert.deepEqual(addon.promptCalls, [["~/work", null]]);
});

test("an idle bracketed CentOS prompt reports its current directory without the bracket", () => {
  const addon = makeAddon(buffer([line("[root@couchdb02 ~]# ")]));
  addon.promptCalls = [];
  addon.onPromptContext = (text, platform) => addon.promptCalls.push([text, platform]);

  addon._reportPromptContext();

  assert.deepEqual(addon.promptCalls, [["~", null]]);
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

test("Cisco IOS and IOS XE prompts report EXEC and configuration submodes", () => {
  const lines = [line("Cisco IOS XE Software, Version 17.12.4"), line("edge-1>")];
  const active = buffer(lines);
  active.cursorY = 1;
  const addon = makeAddon(active);
  addon.promptCalls = [];
  addon.onPromptContext = (text, platform) => addon.promptCalls.push([text, platform]);

  addon._reportPromptContext();
  lines[1] = line("edge-1#");
  addon._reportPromptContext();
  lines[1] = line("edge-1(config-if)#");
  addon._reportPromptContext();

  assert.deepEqual(addon.promptCalls, [
    ["user EXEC", "cisco"],
    ["privileged EXEC", "cisco"],
    ["configure \u00b7 interface", "cisco"],
  ]);
});

test("a saved Cisco platform hint enables a bare IOS prompt", () => {
  const addon = makeAddon(buffer([line("core-1#")]));
  addon.promptCalls = [];
  addon.onPromptContext = (text, platform) => addon.promptCalls.push([text, platform]);

  addon.setPromptPlatform("cisco");
  addon._reportPromptContext();

  assert.deepEqual(addon.promptCalls, [["privileged EXEC", "cisco"]]);
});

test("Cisco IOS XR prompts report route processor, EXEC, and candidate submode", () => {
  const lines = [line("RP/0/RSP0/CPU0:core-1#")];
  const active = buffer(lines);
  const addon = makeAddon(active);
  addon.promptCalls = [];
  addon.onPromptContext = (text, platform) => addon.promptCalls.push([text, platform]);

  addon._reportPromptContext();
  lines[0] = line("RP/0/RSP0/CPU0:core-1(config-bgp-af)#");
  addon._reportPromptContext();
  lines[0] = line("RP/0/RSP0/CPU0:core-1(admin-config)#");
  addon._reportPromptContext();

  assert.deepEqual(addon.promptCalls, [
    ["RP/0/RSP0/CPU0 \u00b7 EXEC", "cisco"],
    ["RP/0/RSP0/CPU0 \u00b7 configure \u00b7 BGP address family", "cisco"],
    ["RP/0/RSP0/CPU0 \u00b7 administration \u00b7 configure", "cisco"],
  ]);
});

test("Cisco-shaped prompts avoid false icon and root-shell detection", () => {
  const lines = [line("leaf-1(config-router)#")];
  const active = buffer(lines);
  const addon = makeAddon(active);
  addon.promptCalls = [];
  addon.onPromptContext = (text, platform) => addon.promptCalls.push([text, platform]);

  addon._reportPromptContext();
  lines[0] = line("server#");
  addon._reportPromptContext();

  assert.deepEqual(addon.promptCalls, [["configure \u00b7 routing", null]]);
});
