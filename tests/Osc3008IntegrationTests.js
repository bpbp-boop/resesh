const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const ghostty = path.join(__dirname, "..", "src", "Terminal", "Ghostty");
const surface = fs.readFileSync(path.join(ghostty, "GhosttyTerminalSurface.cs"), "utf8");
const tracker = fs.readFileSync(path.join(ghostty, "GhosttyCommandTracker.cs"), "utf8");
const tab = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"), "utf8");

test("OSC 3008 crosses the terminal boundary as raw data", () => {
  assert.match(surface, /else if \(code == 3008\)[\s\S]*?OnOsc3008\(payload\)[\s\S]*?ContextReported\?\.Invoke\(payload\)/);
  assert.match(tab, /Osc3008ContextParser\.TryParse[\s\S]*?_workingDirectory\.Observe/);
});

test("OSC 3008 command results enrich Enter-gated marks without replacing discovery", () => {
  const start = tracker.indexOf("public void OnOsc3008(string data)");
  const end = tracker.indexOf("private void RemoveOsc3008", start);
  assert.ok(start >= 0 && end > start);
  const handler = tracker.slice(start, end);
  assert.match(tracker, /Commit\(command\.Row, null, exact: false, probe\.Osc3008Id, command\.Text\)/);
  assert.match(handler, /current\.Exit = p\.Status[\s\S]*?entry\.Exit = code/);
  assert.doesNotMatch(handler, /_oscSeen = true/);
});
