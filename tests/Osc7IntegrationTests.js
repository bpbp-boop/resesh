const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const surface = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "Native", "NativeTerminalSurface.cs"), "utf8");
const tab = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"), "utf8");

test("OSC 7 crosses the terminal boundary as raw data", () => {
  assert.match(surface, /case GhosttyEventKind\.WorkingDirectory:[\s\S]*?WorkingDirectoryReported\?\.Invoke\(directory\)/);
  assert.match(tab, /Osc7WorkingDirectoryParser\.TryParse[\s\S]*?ObserveWorkingDirectory\(report\)/);
  // One tracker takes every report and feeds the tab; nothing reaches the tab raw.
  assert.match(tab, /_workingDirectory\.ObserveReport\(report\);\s*_tab\.ApplyReportedWorkingDirectory\(_workingDirectory\.ReportedPath\)/);
});

test("local current-folder requests rescan the live prompt before process fallback", () => {
  assert.match(surface, /RequestPromptContextAsync\(\) =>\s*Task\.FromResult\(RefreshCommands\(\)\?\.ReportPromptContext\(force: true\)\)/);
  assert.match(tab, /await _terminal\.RequestPromptContextAsync\(\)[\s\S]*?ObservePrompt\([\s\S]*?ResolveLocalTerminalDirectory\(out/);
});

test("terminal-folder selection has the required source priority", () => {
  const start = tab.indexOf("public async Task OpenFilePaneAtCurrentFolderAsync()");
  const end = tab.indexOf("private Interop.SshfsMount", start);
  const method = tab.slice(start, end);
  assert.ok(start >= 0 && end > start);
  // WorkingDirectoryTracker's order: the shell's report, a backend query, the prompt.
  const remote = method.slice(method.indexOf("private async Task<(string? Path, string? Failure)> ResolveTerminalDirectoryAsync()"));
  const order = ["_workingDirectory.ReportedPath", "_persistence.CurrentPathCommand()",
    "RemoteWorkingDirectoryProbe.Command", "_workingDirectory.PromptPath"];
  for (const name of order)
    assert.ok(remote.indexOf(name) >= 0, name);
  for (let index = 1; index < order.length; index++)
    assert.ok(remote.indexOf(order[index - 1]) < remote.indexOf(order[index]), order[index]);
  assert.match(method, /var fallback = Session\.IsLocal \? "the profile folder" : "home"/);
  assert.match(method, /ShowFilePane\(path,[\s\S]*?opened \{fallback\} instead/);
});

test("the native probe never writes to the interactive terminal", () => {
  const start = tab.indexOf("public async Task OpenFilePaneAtCurrentFolderAsync()");
  const end = tab.indexOf("private Interop.SshfsMount", start);
  const method = tab.slice(start, end);
  assert.match(method, /session\.RunCommand\(RemoteWorkingDirectoryProbe\.Command\)/);
  assert.doesNotMatch(method, /_backend\?\.Write|_terminal\.InputReceived/);
});
