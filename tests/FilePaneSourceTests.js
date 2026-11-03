const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const xaml = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml"),
  "utf8");
const terminalView = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"),
  "utf8");
const filePaneView = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "FilePaneView.cs"),
  "utf8");

test("the file pane header jumps to the terminal folder", () => {
  assert.match(filePaneView, /IconButton\([^)]*"Go to terminal folder", \(\) => TerminalFolderRequested\?\.Invoke\(\)\)/);
  assert.match(terminalView, /TerminalFolderRequested \+= \(\) => _ = OpenFilePaneAtCurrentFolderAsync\(\)/);
});

test("file options use consistent title casing", () => {
  // The toggle's own Hide/Show label is checked in TabStripActionStateTests.
  for (const label of [
    "Open File Pane at Terminal Folder",
    "Open in File Explorer",
    "Show File Pane",
  ]) {
    assert.match(xaml, new RegExp(`Text="${label}"`));
  }
});

test("local sessions use the direct filesystem pane and Explorer path", () => {
  assert.match(terminalView, /Session\.IsLocal[\s\S]*?new FilePaneView\(\(\) => Session, OpenInExplorerAsync\)/);
  assert.match(terminalView, /OpenInExplorerAsync\(string path\)[\s\S]*?Session\.IsLocal[\s\S]*?OpenLocalDirectoryInExplorer\(path\)[\s\S]*?SshfsIntegration/);
  assert.match(filePaneView, /_localFiles is not null \|\| SshfsIntegration\.IsInstalled/);
});

test("SFTP downloads validate each remote name and keep recursive paths below the selected folder", () => {
  assert.match(filePaneView, /WindowsDownloadPath\.ValidateName\(entry\.Name\)/);
  assert.match(filePaneView, /PlanRemoteDirectory\(sftp, entry\.FullPath, localPath, targetDir, files, token\)/);
  assert.doesNotMatch(filePaneView, /Path\.Combine\(localDir, child\.Name\)/);
});
