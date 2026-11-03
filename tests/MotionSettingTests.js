const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const appDirectory = path.join(__dirname, "..", "src", "App");

function sourceFiles(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory())
      return entry.name === "bin" || entry.name === "obj" ? [] : sourceFiles(full);
    return entry.name.endsWith(".cs") ? [full] : [];
  });
}

// Motion follows Windows' "Animation effects" setting, read in one place so a new
// animation can't forget it or read it differently.
test("only AppMotion reads the Windows animation setting", () => {
  const readers = sourceFiles(appDirectory)
    .filter(file => /AnimationsEnabled/.test(fs.readFileSync(file, "utf8")))
    .map(file => path.relative(appDirectory, file));
  assert.deepEqual(readers, ["AppMotion.cs"]);
});

test("every code-driven animation checks AppMotion", () => {
  for (const file of sourceFiles(appDirectory)) {
    const source = fs.readFileSync(file, "utf8");
    if (!/new Storyboard\(|\.StartAnimation\(/.test(source))
      continue;
    assert.match(source, /AppMotion\.Enabled|PaneMotionEnabled|\bMotionEnabled\b/, path.relative(appDirectory, file));
  }
  const rail = fs.readFileSync(path.join(appDirectory, "MainWindow.Rail.cs"), "utf8");
  const start = rail.slice(rail.indexOf("private void StartSessionsPaneAnimation("));
  assert.match(start.slice(0, start.indexOf("new Storyboard(")), /if \(!AppMotion\.Enabled\)/);
});
