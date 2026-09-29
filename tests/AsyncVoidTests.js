const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const src = path.join(__dirname, "..", "src");

function* sourceFiles(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name !== "bin" && entry.name !== "obj")
        yield* sourceFiles(path.join(dir, entry.name));
    } else if (entry.name.endsWith(".cs")) {
      yield path.join(dir, entry.name);
    }
  }
}

test("async void is reserved for event handlers", () => {
  // An exception escaping an async void method ends the app; everything else returns a Task.
  const offenders = [];
  for (const file of sourceFiles(src)) {
    const text = fs.readFileSync(file, "utf8");
    for (const match of text.matchAll(/async void (\w+)\(([^)]*)\)/g)) {
      const parameters = match[2].split(",").map(parameter => parameter.trim());
      const isHandler = parameters.length === 2 && /\w*Args\s+\w+$/.test(parameters[1]);
      if (!isHandler)
        offenders.push(`${path.relative(src, file)}: ${match[1]}`);
    }
  }
  assert.deepEqual(offenders, []);
});
