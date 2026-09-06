"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const root = path.join(__dirname, "..", "src", "Terminal");

test("the in-memory terminal page includes every local script and stylesheet in page order", () => {
  const html = fs.readFileSync(path.join(root, "wwwroot", "terminal.html"), "utf8");
  const host = fs.readFileSync(path.join(root, "TerminalControl.cs"), "utf8");
  const assetBlock = host.slice(host.indexOf("var assets ="), host.indexOf("var page ="));
  const bundled = [...assetBlock.matchAll(/, "([^"\n]+\.(?:js|css))",/g)].map(match => match[1]);
  const required = [...html.matchAll(/<(?:script src|link rel="stylesheet" href)="([^"/:]+)"/g)]
    .map(match => match[1]);
  assert.ok(required.length > 0);
  assert.deepEqual(bundled, required, "NavigateToString cannot load relative assets left outside the bundle");
  for (const file of bundled) {
    const content = fs.readFileSync(path.join(root, "wwwroot", file), "utf8");
    assert.ok(content.length > 0, `${file} must be shipped`);
    assert.doesNotMatch(content, file.endsWith(".js") ? /<\/script>/i : /<\/style>/i);
  }
});
