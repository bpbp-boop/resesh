const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const wwwroot = path.join(__dirname, "..", "src", "Terminal", "wwwroot");
const rulerSource = fs.readFileSync(path.join(wwwroot, "addon-ruler.js"), "utf8");
const pageSource = fs.readFileSync(path.join(wwwroot, "terminal.html"), "utf8");

const window = { devicePixelRatio: 1 };
vm.runInNewContext(rulerSource, { window, Map, Math, RegExp, Set, requestAnimationFrame() {} });
const RulerAddon = window.RulerAddon.RulerAddon;

/** A terminal whose buffer is a fixed array of lines, with the cursor on the last one. */
function fakeTerm(lines) {
  const cursorY = lines.length - 1;
  return {
    rows: 20,
    buffer: {
      active: {
        type: "normal",
        baseY: 0,
        cursorY,
        length: lines.length,
        getLine: (index) =>
          lines[index] === undefined
            ? null
            : { isWrapped: false, translateToString: () => lines[index] },
      },
    },
    registerMarker: (offset) => ({
      line: cursorY + offset,
      isDisposed: false,
      onDispose() {},
      dispose() {},
    }),
  };
}

/** Marks a command at the last line and returns what the agent hook was told. */
function observeCommand(promptLine) {
  const addon = new RulerAddon();
  addon._term = fakeTerm([promptLine]);
  const seen = [];
  addon.onCommandMark((command) => seen.push(command));
  addon._cmdCommit(0, null, "guess");
  return seen;
}

test("the command hook reports the command, not the prompt", () => {
  assert.deepEqual(observeCommand("bpg@host:~/src$ claude --resume"), ["claude --resume"]);
  assert.deepEqual(observeCommand("[bpg@host ~]# codex exec 'fix it'"), ["codex exec 'fix it'"]);
  assert.deepEqual(observeCommand("sw1#show ip int brief"), ["show ip int brief"]);
});

test("PowerShell and cmd prompts are recognized, so local tabs get command marks too", () => {
  assert.deepEqual(observeCommand("PS C:\\Users\\Boden> claude"), ["claude"]);
  assert.deepEqual(observeCommand("C:\\Users\\Boden>gemini"), ["gemini"]);
});

test("a line with no prompt shape still reports its text once marked", () => {
  // OSC 133 marks are committed by the shell, not by the regex; whatever is on the
  // prompt row is the best sample available.
  assert.deepEqual(observeCommand("claude"), ["claude"]);
});

test("re-committing the same line does not report the command twice", () => {
  const addon = new RulerAddon();
  addon._term = fakeTerm(["bpg@host:~$ claude"]);
  const seen = [];
  addon.onCommandMark((command) => seen.push(command));
  addon._cmdCommit(0, null, "osc");
  addon._cmdCommit(0, 0, "osc"); // the exit code arriving must not re-fire
  assert.deepEqual(seen, ["claude"]);
});

test("a throwing listener cannot break command marking", () => {
  const addon = new RulerAddon();
  addon._term = fakeTerm(["bpg@host:~$ claude"]);
  addon.onCommandMark(() => {
    throw new Error("boom");
  });
  assert.doesNotThrow(() => addon._cmdCommit(0, null, "guess"));
  assert.equal(addon._cmdMarks.length, 1);
});

test("the page forwards agent evidence and nothing else", () => {
  // OSC 7377 (resesh structured events) plus the two generic notification sequences.
  assert.match(pageSource, /\[7377, 9, 777\]\.forEach/);
  assert.match(pageSource, /type: "agentOsc"/);
  assert.match(pageSource, /type: "agentBell"/);
  assert.match(pageSource, /type: "title"/);
  assert.match(pageSource, /type: "command"/);
  // Payloads from the wire are length-capped before they cross into the host.
  assert.match(pageSource, /data: String\(data == null \? "" : data\)\.slice\(0, 2048\)/);
  assert.match(pageSource, /String\(title \|\| ""\)\.slice\(0, 512\)/);
});

test("the page maps no attention state of its own", () => {
  // Every mapping decision belongs to the native tracker, where it is tested.
  assert.doesNotMatch(pageSource, /needs-approval|needs-answer/);
});

test("TerminalTabView wires running command changes and prompt context to retire stale agents", () => {
  const terminalTabView = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"),
    "utf8");

  // CommandChanged (runningCommand: text on start, "" on 133;D end) feeds agent tracking
  assert.match(
    terminalTabView,
    /_terminal\.CommandChanged \+= command => ApplyAgent\(tracker => tracker\.ObserveCommand\(command\)\);/);

  // PromptContextChanged (reaching an idle prompt) feeds agent tracking as command end
  assert.match(
    terminalTabView,
    /_terminal\.PromptContextChanged \+= \(_, _\) => ApplyAgent\(tracker => tracker\.ObserveCommand\(""\)\);/);

  // TitleChanged and CommandObserved remain wired
  assert.match(
    terminalTabView,
    /_terminal\.TitleChanged \+= title => ApplyAgent\(tracker => tracker\.ObserveTitle\(title\)\);/);
  assert.match(
    terminalTabView,
    /_terminal\.CommandObserved \+= command => ApplyAgent\(tracker => tracker\.ObserveCommand\(command\)\);/);
});
