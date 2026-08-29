const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const ruler = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "wwwroot", "addon-ruler.js"), "utf8");
const page = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "wwwroot", "terminal.html"), "utf8");
const control = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "TerminalControl.cs"), "utf8");
const tab = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"), "utf8");

test("OSC 3008 crosses the terminal boundary as bounded raw data", () => {
  assert.match(ruler, /registerOscHandler\(3008,[\s\S]*?length <= 4096[\s\S]*?onContext/);
  assert.match(page, /onContext[\s\S]*?type: "osc3008"[\s\S]*?slice\(0, 4096\)/);
  assert.match(control, /case "osc3008"[\s\S]*?ContextReported\?\.Invoke/);
  assert.match(tab, /Osc3008ContextParser\.TryParse[\s\S]*?_workingDirectory\.Observe/);
});

test("OSC 3008 command results enrich Enter-gated marks without replacing discovery", () => {
  assert.match(ruler, /_cmdCommit\(command\.row, null, "guess", marker\._osc3008Id, command\.text\)/);
  assert.match(ruler, /current\.exit = parsed\.status[\s\S]*?current\.entry\.exit/);
  assert.doesNotMatch(ruler, /_onOsc3008[\s\S]{0,500}_cmdOscSeen = true/);
});
