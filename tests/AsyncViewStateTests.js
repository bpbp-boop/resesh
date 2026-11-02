const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

// Views that start background work keep only the newest result and drop work that finishes
// after they close. These checks pin the guards in place.
const root = path.join(__dirname, "..", "src", "App");
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), "utf8");

function methodBody(source, signature) {
  const start = source.indexOf(signature);
  assert.notEqual(start, -1, `missing ${signature}`);
  const open = source.indexOf("{", start);
  let depth = 0;
  for (let index = open; index < source.length; index++) {
    if (source[index] === "{")
      depth++;
    else if (source[index] === "}" && --depth === 0)
      return source.slice(open, index + 1);
  }
  throw new Error(`unbalanced ${signature}`);
}

test("history searches drop a waiting debounce and dispose their cancellation", () => {
  const history = read("Controls", "HistoryView.xaml.cs");
  const search = methodBody(history, "private async Task RunSearchAsync(");
  assert.match(search, /_searchDebounce\?\.Stop\(\)/);
  assert.match(search, /finally[\s\S]*cancellation\.Dispose\(\)/);
});

test("history loads apply only the newest result", () => {
  const history = read("Controls", "HistoryView.xaml.cs");
  const load = methodBody(history, "private async Task LoadAsync(");
  assert.match(load, /var version = \+\+_loadVersion;/);
  assert.match(load, /version != _loadVersion \|\| !IsOpen/);
  assert.match(methodBody(history, "public void Close()"), /_loadVersion\+\+/);
});
