const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const ruler = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "wwwroot", "addon-ruler.js"), "utf8");
const page = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "wwwroot", "terminal.html"), "utf8");
const control = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "TerminalControl.cs"), "utf8");
const tab = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"), "utf8");

test("OSC 7 crosses the terminal boundary as bounded raw data", () => {
  assert.match(ruler, /registerOscHandler\(7,[\s\S]*?onWorkingDirectory[\s\S]*?slice\(0, 2048\)/);
  assert.match(page, /onWorkingDirectory[\s\S]*?type: "workingDirectory"[\s\S]*?slice\(0, 2048\)/);
  assert.match(control, /case "workingDirectory"[\s\S]*?WorkingDirectoryReported\?\.Invoke/);
  assert.match(tab, /Osc7WorkingDirectoryParser\.TryParse[\s\S]*?_workingDirectory\.Observe/);
});

test("terminal-folder selection has the required source priority", () => {
  const start = tab.indexOf("public async Task OpenFilePaneAtCurrentFolderAsync()");
  const end = tab.indexOf("private Interop.SshfsMount", start);
  const method = tab.slice(start, end);
  assert.ok(start >= 0 && end > start);
  assert.ok(method.indexOf("TmuxPersistence.CurrentPathCommand") < method.indexOf("_workingDirectory.Path"));
  assert.ok(method.indexOf("_workingDirectory.Path") < method.indexOf("RemoteWorkingDirectoryProbe.Command"));
  assert.ok(method.indexOf("RemoteWorkingDirectoryProbe.Command") < method.indexOf("_tab.PromptContext"));
  assert.match(method, /ShowFilePane\(path,[\s\S]*?opened home instead/);
});

test("the native probe never writes to the interactive terminal", () => {
  const start = tab.indexOf("public async Task OpenFilePaneAtCurrentFolderAsync()");
  const end = tab.indexOf("private Interop.SshfsMount", start);
  const method = tab.slice(start, end);
  assert.match(method, /session\.RunCommand\(RemoteWorkingDirectoryProbe\.Command\)/);
  assert.doesNotMatch(method, /_backend\?\.Write|_terminal\.InputReceived/);
});
