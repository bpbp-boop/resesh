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

const handler = page.match(/    if \(term\.parser && term\.parser\.registerOscHandler\) {\s*term\.parser\.registerOscHandler\(52,[\s\S]*?\r?\n    }\r?\n/);
assert.ok(handler, "the OSC 52 handler should exist");
const b64ToBytes = page.match(/function b64ToBytes\(b64\)\s*{[\s\S]*?\r?\n  }/);
assert.ok(b64ToBytes, "b64ToBytes should exist");

function setup(readOnly) {
  const term = new Terminal({ allowProposedApi: true });
  const posted = [];
  const context = vm.createContext({
    term, readOnly, atob, TextDecoder,
    host: { postMessage: message => posted.push(message) },
    pageTrace: () => {},
  });
  vm.runInContext(b64ToBytes[0] + "\n" + handler[0], context);
  const write = data => new Promise(resolve => term.write(data, resolve));
  return { term, posted, write };
}

const b64 = text => Buffer.from(text, "utf8").toString("base64");

test("OSC 52 writes decode UTF-8 and reach the host clipboard", async () => {
  const { term, posted, write } = setup(false);
  try {
    await write(`\x1b]52;c;${b64("héllo\nwörld ✓")}\x07`);
    await write(`\x1b]52;;${b64("empty selection")}\x1b\\`);
    assert.deepEqual(posted, [
      { type: "copy", text: "héllo\nwörld ✓" },
      { type: "copy", text: "empty selection" },
    ]);
    assert.equal(term.buffer.active.getLine(0).translateToString(true), "", "nothing is printed");
  } finally {
    term.dispose();
  }
});

test("OSC 52 read queries, clears and malformed payloads are dropped", async () => {
  const { term, posted, write } = setup(false);
  try {
    await write("\x1b]52;c;?\x07\x1b]52;c;\x07\x1b]52\x07\x1b]52;c;!!not base64!!\x07");
    assert.deepEqual(posted, []);
  } finally {
    term.dispose();
  }
});

test("read-only playback never sets the clipboard", async () => {
  const { term, posted, write } = setup(true);
  try {
    await write(`\x1b]52;c;${b64("recorded")}\x07`);
    assert.deepEqual(posted, []);
  } finally {
    term.dispose();
  }
});
