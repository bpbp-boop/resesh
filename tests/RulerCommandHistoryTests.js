const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const test = require("node:test");
const { Terminal } = require("../src/Terminal/wwwroot/xterm.js");

const window = { devicePixelRatio: 1 };
vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../src/Terminal/wwwroot/addon-ruler.js"), "utf8"), {
  window, Map, Math, RegExp, Set, Date, requestAnimationFrame() {}, setTimeout, clearTimeout,
});
const pageSource = fs.readFileSync(path.join(__dirname, "../src/Terminal/wwwroot/terminal.html"), "utf8");
const hostSource = fs.readFileSync(path.join(__dirname, "../src/Terminal/TerminalControl.cs"), "utf8");
const tabSource = fs.readFileSync(path.join(__dirname, "../src/App/Terminal/TerminalTabView.cs"), "utf8");

const osc = data => "\x1b]133;" + data + "\x07";
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

function setup(options = {}) {
  const term = new Terminal({ cols: 60, rows: 10, scrollback: 100, allowProposedApi: true, ...options });
  const addon = new window.RulerAddon.RulerAddon();
  addon._term = term;
  addon._paintQueued = true;
  const records = [];
  addon.onCommandRecord = record => records.push(JSON.parse(JSON.stringify(record)));
  addon.setHistoryCapture(true);
  term.parser.registerOscHandler(133, data => { addon._onOsc133(data); return true; });
  const write = data => new Promise(resolve => term.write(data, resolve));
  const start = command => write(osc("A") + "$ " + osc("B") + command + "\r\n" + osc("C"));
  // A discovered command: the prompt line as the Enter probe would find it.
  const guess = async (command) => {
    await write("user@host:~$ " + command);
    const buf = term.buffer.active;
    const row = buf.baseY + buf.cursorY;
    await write("\r\n");
    const entry = addon._cmdCommit(row, null, "guess", undefined, command);
    entry.fullText = command;
    return entry;
  };
  return { term, addon, records, write, start, guess };
}

test("shell integration records a command with its output and exit code when D arrives", async () => {
  const s = setup();
  try {
    await s.start("ls -la");
    await s.write("total 0\r\nfile.txt\r\n");
    await s.write(osc("D;0"));
    assert.equal(s.records.length, 1);
    const record = s.records[0];
    assert.equal(record.command, "ls -la");
    assert.equal(record.exit, 0);
    assert.equal(record.exact, true);
    assert.equal(record.output, "total 0\nfile.txt");
    assert.equal(record.lost, false);
    assert.equal(record.truncated, false);
    assert.ok(record.endedMs >= record.startedMs);
  } finally { s.term.dispose(); }
});

test("a failed command keeps its exit code and every command is recorded once", async () => {
  const s = setup();
  try {
    await s.start("false");
    await s.write(osc("D;1") + osc("D;0"));
    await s.start("true");
    await s.write(osc("D;0"));
    assert.deepEqual(s.records.map(r => [r.command, r.exit, r.output]), [["false", 1, ""], ["true", 0, ""]]);
  } finally { s.term.dispose(); }
});

test("history stays off until the host turns it on", async () => {
  const s = setup();
  try {
    s.addon.setHistoryCapture(false);
    await s.start("secret-thing");
    await s.write("password123\r\n" + osc("D;0"));
    assert.deepEqual(s.records, []);
  } finally { s.term.dispose(); }
});

test("turning history off drops the command still running", async () => {
  const s = setup();
  try {
    await s.start("tail -f log");
    await s.write("line\r\n");
    s.addon.setHistoryCapture(false);
    s.addon.setHistoryCapture(true);
    await s.write(osc("D;130"));
    assert.deepEqual(s.records, []);
  } finally { s.term.dispose(); }
});

test("a discovered command ends where the next command starts", async () => {
  const s = setup();
  try {
    await s.guess("uname -a");
    await s.write("Linux web01 6.8.0\r\n");
    await s.guess("uptime");
    assert.equal(s.records.length, 1);
    assert.equal(s.records[0].command, "uname -a");
    assert.equal(s.records[0].output, "Linux web01 6.8.0");
    assert.equal(s.records[0].exit, null);
    assert.equal(s.records[0].exact, false);
  } finally { s.term.dispose(); }
});

test("a discovered command is recorded once output goes quiet at an idle prompt", async () => {
  const s = setup();
  try {
    await s.guess("df -h");
    await s.write("Filesystem Size\r\n/dev/sda1 40G\r\nuser@host:~$ ");
    s.addon._historyScheduleIdle();
    await sleep(700);
    assert.equal(s.records.length, 1);
    assert.equal(s.records[0].output, "Filesystem Size\n/dev/sda1 40G");
  } finally { s.term.dispose(); }
});

test("output that is still arriving does not end a discovered command", async () => {
  const s = setup();
  try {
    await s.guess("ping host");
    await s.write("64 bytes from host\r\n");
    s.addon._historyScheduleIdle();
    await sleep(700);
    assert.deepEqual(s.records, []);
  } finally { s.term.dispose(); }
});

test("flushHistory records a running command with the output so far", async () => {
  const s = setup();
  try {
    await s.start("tail -f app.log");
    await s.write("started\r\nlistening on :8080\r\n");
    s.addon.flushHistory();
    s.addon.flushHistory();
    assert.equal(s.records.length, 1);
    assert.equal(s.records[0].output, "started\nlistening on :8080");
    assert.equal(s.records[0].exit, null);
  } finally { s.term.dispose(); }
});

test("a command trimmed out of scrollback keeps its line and reports the output lost", async () => {
  const s = setup({ rows: 3, scrollback: 3 });
  try {
    await s.start("seq 100");
    await s.write("n\r\n".repeat(20));
    assert.equal(s.records.length, 1);
    assert.equal(s.records[0].command, "seq 100");
    assert.equal(s.records[0].lost, true);
    assert.equal(s.records[0].output, "");
  } finally { s.term.dispose(); }
});

test("long output is cut to 64K characters and marked truncated", async () => {
  const s = setup({ cols: 200, scrollback: 2000 });
  try {
    await s.start("cat big");
    await s.write(("x".repeat(199) + "\r\n").repeat(400));
    await s.write(osc("D;0"));
    assert.equal(s.records[0].output.length, 65536);
    assert.equal(s.records[0].truncated, true);
  } finally { s.term.dispose(); }
});

test("history keeps command lines longer than the 256-character label", async () => {
  const s = setup({ cols: 80 });
  try {
    const command = "curl -H 'X-Header: " + "a".repeat(400) + "' https://example.test";
    await s.start(command);
    await s.write(osc("D;0"));
    assert.equal(s.records[0].command, command);
    assert.equal(s.addon._cmdMarks[0].text.length, 256);
  } finally { s.term.dispose(); }
});

test("disposing the addon records the open command first", async () => {
  const s = setup();
  try {
    await s.start("sleep 100");
    s.addon.dispose();
    assert.equal(s.records.length, 1);
    assert.equal(s.records[0].command, "sleep 100");
  } finally { s.term.dispose(); }
});

test("the page forwards records, flushes before the disconnect divider, and gates capture on live tabs", () => {
  assert.match(pageSource, /ruler\.onCommandRecord = function \(record\)/);
  assert.match(pageSource, /if \(readOnly \|\| !record/);
  assert.match(pageSource, /case "disconnected":\s*connected = false;\s*ruler\.flushHistory\(\);/);
  assert.match(pageSource, /case "setHistoryCapture":\s*ruler\.setHistoryCapture\(msg\.enabled === true && !readOnly\)/);
  assert.match(hostSource, /case "commandRecord":/);
  assert.match(tabSource, /_terminal\.SetHistoryCapture\(initial\.KeepCommandHistory\)/);
  assert.match(tabSource, /_terminal\.SetHistoryCapture\(effective\.KeepCommandHistory\)/);
});

test("the folder comes from the command's own prompt, not the prompt after a cd", async () => {
  const s = setup();
  try {
    await s.write(String.raw`C:\Users\me>cd C:\Windows`);
    const buf = s.term.buffer.active;
    const row = buf.baseY + buf.cursorY;
    await s.write("\r\n\r\nC:\Windows>");
    s.addon._cmdCommit(row, null, "guess", undefined, String.raw`cd C:\Windows`);
    s.addon.flushHistory();
    await s.write("\r\n");
    await s.guess("ls");
    await s.write("a.txt\r\n");
    s.addon.flushHistory();
    assert.deepEqual(s.records.map(r => r.directory), [String.raw`C:\Users\me`, "~"]);
  } finally { s.term.dispose(); }
});
