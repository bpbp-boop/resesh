const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const surfaceSource = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "Native", "NativeTerminalSurface.cs"),
  "utf8");

test("the terminal surface forwards agent evidence with bounded payloads", () => {
  // OSC 7377 (resesh structured events) plus the two generic notification sequences.
  assert.match(surfaceSource, /if \(code is 7377 or 9 or 777\)/);
  // Payloads from the wire are length-capped before they reach the host.
  assert.match(surfaceSource, /AgentOscReceived\?\.Invoke\(code, payload\.Length > 2048 \? payload\[\.\.2048\] : payload\)/);
  assert.match(surfaceSource, /private static string Cap\(string text\) => text\.Length > 512 \? text\[\.\.512\] : text;/);
  assert.match(surfaceSource, /TitleChanged\?\.Invoke\(Cap\(/);
  assert.match(surfaceSource, /CommandObserved\?\.Invoke\(Cap\(command\)\)/);
});

test("the terminal surface maps no attention state of its own", () => {
  // Every mapping decision belongs to the native tracker, where it is tested.
  assert.doesNotMatch(surfaceSource, /needs-approval|needs-answer/);
});

test("TerminalTabView wires running command changes and prompt context to retire stale agents", () => {
  const terminalTabView = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"),
    "utf8");

  // CommandChanged (runningCommand: text on start, "" on 133;D end) feeds agent tracking
  assert.match(
    terminalTabView,
    /_terminal\.CommandChanged \+= \(command, _\) => ApplyAgent\(tracker => tracker\.ObserveCommand\(command\)\);/);

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

test("running command changes keep their exact lifecycle provenance", () => {
  assert.match(
    surfaceSource,
    /_commands\.RunningCommand \+= \(text, exact\) => CommandChanged\?\.Invoke\(Cap\(text\), exact\);/);
});
