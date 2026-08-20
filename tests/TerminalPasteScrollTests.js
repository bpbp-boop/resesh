"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");

const page = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "wwwroot", "terminal.html"),
  "utf8"
);

assert.match(page, /function returnToLiveInput\(\)\s*{\s*term\.scrollToBottom\(\);\s*}/);
assert.match(page, /container\.addEventListener\("paste", returnToLiveInput, true\)/);

const pasteHelper = page.match(/function pasteText\(text\)\s*{([\s\S]*?)\n    }/);
assert.ok(pasteHelper, "pasteText helper should exist");
assert.match(pasteHelper[1], /returnToLiveInput\(\)/);

const helperUses = page.match(/pasteText\(text\);/g) || [];
assert.strictEqual(helperUses.length, 2, "right-click and Ctrl+Shift+V should share paste behavior");

console.log("Terminal paste scroll tests passed.");
