"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("node:vm");
const test = require("node:test");
const { Terminal } = require("../src/Terminal/wwwroot/xterm.js");

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

test("host clipboard pastes use negotiated bracketed paste without submitting lines", async () => {
  const term = new Terminal();
  // The paste API clears the textarea; no rendered DOM is needed for encoding input.
  term._core.textarea = { value: "" };
  const sent = [];
  term.onData(data => sent.push(data));
  let scrolls = 0;
  const context = vm.createContext({
    term, connected: true, readOnly: false,
    returnToLiveInput: () => scrolls++,
  });
  vm.runInContext(pasteHelper[0], context);
  const text = "first\r\nsecond\n" + "long line\n".repeat(10000);
  context.text = text;
  try {
    await new Promise(resolve => term.write("\x1b[?2004h", resolve));
    vm.runInContext("pasteText(text)", context);
    assert.deepEqual(sent, ["\x1b[200~" + text.replace(/\r?\n/g, "\r") + "\x1b[201~"]);
    assert.equal(scrolls, 1);

    sent.length = 0;
    await new Promise(resolve => term.write("\x1b[?2004l", resolve));
    vm.runInContext('pasteText("first\\r\\nsecond")', context);
    assert.deepEqual(sent, ["first\rsecond"]);

    sent.length = 0;
    vm.runInContext('readOnly = true; pasteText(text); readOnly = false; connected = false; pasteText(text); connected = true; pasteText("")', context);
    assert.deepEqual(sent, []);
    assert.equal(scrolls, 2);
  } finally {
    term.dispose();
  }
});
