const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const test = require("node:test");
const { Terminal } = require("../src/Terminal/wwwroot/xterm.js");
const window = { devicePixelRatio: 1 };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../src/Terminal/wwwroot/addon-ruler.js"), "utf8"), {
  window, Map, Math, RegExp, Set, requestAnimationFrame() {}, setTimeout, clearTimeout,
});
const osc = data => "\x1b]133;" + data + "\x07";
function setup(options = {}) {
  const term = new Terminal({ cols: 50, rows: 3, scrollback: 3, allowProposedApi: true, ...options });
  const addon = new window.RulerAddon.RulerAddon();
  addon._term = term;
  addon._paintQueued = true;
  const events = [];
  addon.onCommandExecution = event => events.push(JSON.parse(JSON.stringify(event)));
  term.parser.registerOscHandler(133, data => { addon._onOsc133(data); return true; });
  const write = data => new Promise(resolve => term.write(data, resolve));
  const start = command => write(osc("A") + "$ " + osc("B") + command + "\r\n" + osc("C"));
  return { term, addon, events, write, start };
}

test("exact C/D events share monotonic IDs and duplicate D does not finish twice", async () => {
  const s = setup();
  try {
    await s.start("false");
    await s.write(osc("C") + osc("D;1") + osc("D;0"));
    await s.start("true");
    await s.write(osc("D;0"));
    assert.deepEqual(s.events, [
      { id: 1, commandLine: "false", completed: false, exitCode: null },
      { id: 1, commandLine: "false", completed: true, exitCode: 1 },
      { id: 2, commandLine: "true", completed: false, exitCode: null },
      { id: 2, commandLine: "true", completed: true, exitCode: 0 },
    ]);
    assert.deepEqual(Array.from(s.addon._cmdMarks, mark => mark.executionId), [1, 2]);
  } finally { s.term.dispose(); }
});

test("missing D finishes unknown on A once, never claims success or finishes the next command", async () => {
  const s = setup();
  try {
    await s.start("work");
    await s.write(osc("A") + osc("A") + osc("D;7"));
    assert.deepEqual(s.events[1], { id: 1, commandLine: "work", completed: true, exitCode: null });
    assert.equal(s.events.length, 2);
    await s.start("next");
    assert.equal(s.events.length, 3);
    await s.write(osc("D"));
    assert.deepEqual(s.events[3], { id: 2, commandLine: "next", completed: true, exitCode: null });
  } finally { s.term.dispose(); }
});

test("invalid status does not finish an execution; prompt fallback remains unknown", async () => {
  const s = setup();
  try {
    await s.start("work");
    await s.write(osc("D;garbage"));
    assert.equal(s.events.length, 1);
    await s.write(osc("A"));
    assert.equal(s.events[1].exitCode, null);
  } finally { s.term.dispose(); }
});

test("navigation resolves execution IDs through live markers and trimming does not lose completion", async () => {
  const s = setup();
  try {
    await s.start("long-work");
    const jumps = [];
    s.addon.jumpToCommand = row => { jumps.push(row); return true; };
    assert.equal(s.addon.jumpToExecution(1), true);
    assert.deepEqual(jumps, [0]);
    assert.equal(s.addon.jumpToExecution(999), false);
    await s.write("output\r\n".repeat(15));
    assert.equal(s.addon.jumpToExecution(1), false);
    await s.write(osc("D;7"));
    assert.equal(s.events[1].id, 1);
    assert.equal(s.events[1].exitCode, 7);
  } finally { s.term.dispose(); }
});

test("read-only playback and disconnect cancellation suppress lifecycle without resetting IDs", async () => {
  const s = setup();
  try {
    s.addon.setExecutionReporting(false);
    await s.start("recorded");
    await s.write(osc("D;0"));
    assert.deepEqual(s.events, []);
    s.addon.setExecutionReporting(true);
    await s.start("cancelled");
    s.addon.setExecutionReporting(false);
    await s.write(osc("D;0") + osc("A"));
    assert.equal(s.events.length, 1);
    s.addon.setExecutionReporting(true);
    await s.write(osc("D;9"));
    await s.start("new-connection");
    await s.write(osc("D;0"));
    assert.deepEqual(s.events.map(e => [e.id, e.commandLine, e.completed]),
      [[1, "cancelled", false], [2, "new-connection", false], [2, "new-connection", true]]);
  } finally { s.term.dispose(); }
});

test("empty C and discovery marks never emit lifecycle events", async () => {
  const s = setup();
  try {
    await s.start("");
    await s.write(osc("D;0"));
    s.addon._cmdCommit(0, 0, "guess", undefined, "discovered");
    assert.deepEqual(s.events, []);
  } finally { s.term.dispose(); }
});

test("fragmented OSC input produces one matching lifecycle", async () => {
  const s = setup();
  try {
    const input = osc("A") + "$ " + osc("B") + "printf done\r\n" + osc("C") + osc("D;0");
    for (const character of input) await s.write(character);
    assert.equal(s.events.length, 2);
    assert.equal(s.events[0].id, s.events[1].id);
    assert.equal(s.events[0].commandLine, "printf done");
  } finally { s.term.dispose(); }
});

test("enabling after the initial prompt retains the first command boundary", async () => {
  const s = setup();
  try {
    s.addon.setExecutionReporting(false);
    await s.write(osc("A") + "$ " + osc("B"));
    s.addon.setExecutionReporting(true);
    await s.write("first\r\n" + osc("C") + osc("D;0"));
    assert.deepEqual(s.events, [
      { id: 1, commandLine: "first", completed: false, exitCode: null },
      { id: 1, commandLine: "first", completed: true, exitCode: 0 },
    ]);
  } finally { s.term.dispose(); }
});
