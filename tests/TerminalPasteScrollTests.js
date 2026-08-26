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

assert.match(page, /case "paste":\s*pasteText\(/, "the native clipboard response should use pasteText");

console.log("Terminal paste scroll tests passed.");
