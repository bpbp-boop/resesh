"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("node:vm");
const test = require("node:test");
const wwwroot = path.join(__dirname, "..", "src", "Terminal", "wwwroot");
const { Terminal } = require(path.join(wwwroot, "xterm.js"));
const { Unicode11Addon } = require(path.join(wwwroot, "addon-unicode11.js"));

const page = fs.readFileSync(path.join(wwwroot, "terminal.html"), "utf8");

test("the page measures emoji with Unicode 11 widths, as remote wcwidth does", async () => {
  assert.match(page, /term\.loadAddon\(new Unicode11Addon\.Unicode11Addon\(\)\);\s*term\.unicode\.activeVersion = "11";/);
  const term = new Terminal({ allowProposedApi: true });
  try {
    term.loadAddon(new Unicode11Addon());
    term.unicode.activeVersion = "11";
    await new Promise(resolve => term.write("👍|漢|✓|", resolve));
    const line = term.buffer.active.getLine(0);
    const widths = [0, 2, 3, 5, 6, 7].map(x => [line.getCell(x).getChars(), line.getCell(x).getWidth()]);
    assert.deepEqual(widths, [["👍", 2], ["|", 1], ["漢", 2], ["|", 1], ["✓", 1], ["|", 1]]);
    assert.equal(term.buffer.active.cursorX, 8);
  } finally {
    term.dispose();
  }
});

test("OSC 8 hyperlinks use the native HTTP(S)-only open path and show their real target", () => {
  const handler = page.match(/linkHandler: {\s*([\s\S]*?)\r?\n      }/);
  assert.ok(handler, "the terminal should set an OSC 8 linkHandler");
  const posted = [];
  const container = { title: "" };
  const linkHandler = vm.runInNewContext(`({ ${handler[1]} })`, {
    host: { postMessage: message => posted.push(message) }, container,
  });
  linkHandler.hover({}, "https://example.com/real");
  assert.equal(container.title, "https://example.com/real");
  linkHandler.activate({}, "https://example.com/real");
  assert.deepEqual(JSON.parse(JSON.stringify(posted)), [{ type: "openLink", uri: "https://example.com/real" }]);
  linkHandler.leave();
  assert.equal(container.title, "");
});

test("Shift+Enter sends ESC CR on the normal screen only", () => {
  const helper = page.match(/function isShiftEnter\(e\)\s*{[\s\S]*?\r?\n    }/);
  assert.ok(helper, "isShiftEnter should exist");
  assert.match(page, /if \(isShiftEnter\(e\)\) {\s*if \(e\.type === "keydown"\) {\s*e\.preventDefault\(\);\s*term\.input\("\\x1b\\r", true\);\s*}\s*return false;\s*}/,
    "the key handler should send ESC CR once on keydown and swallow the keypress");
  const term = { buffer: { active: { type: "normal" } } };
  const context = vm.createContext({ term });
  vm.runInContext(helper[0], context);
  const is = e => context.isShiftEnter(Object.assign({ key: "Enter", code: "Enter", shiftKey: false, ctrlKey: false, altKey: false, metaKey: false }, e));

  assert.equal(is({ shiftKey: true }), true);
  assert.equal(is({ shiftKey: true, code: "NumpadEnter" }), true);
  assert.equal(is({}), false, "plain Enter still submits");
  assert.equal(is({ shiftKey: true, ctrlKey: true }), false);
  assert.equal(is({ shiftKey: true, altKey: true }), false);
  assert.equal(is({ shiftKey: true, key: "A", code: "KeyA" }), false);
  term.buffer.active.type = "alternate";
  assert.equal(is({ shiftKey: true }), false, "vim insert mode must not receive ESC");
});

test("Meta+Enter is not treated as submitting a command", () => {
  assert.match(page, /if \(data\.indexOf\("\\r"\) >= 0 && data !== "\\x1b\\r"\) ruler\.notifyEnter\(titlesSeen\);/);
});

test("XTVERSION names the terminal, so Claude Code goes on to ask for synchronized output", async () => {
  const handler = page.match(/    if \(term\.parser && term\.parser\.registerCsiHandler\) {\s*term\.parser\.registerCsiHandler\({ prefix: ">", final: "q" }[\s\S]*?\r?\n    }\r?\n/);
  assert.ok(handler, "the XTVERSION handler should exist");
  const term = new Terminal({ allowProposedApi: true });
  const replies = [];
  term.onData(data => replies.push(data));
  vm.runInContext(handler[0], vm.createContext({ term }));
  const write = data => new Promise(resolve => term.write(data, resolve));
  try {
    await write("\x1b[>q");
    await write("\x1b[>0q");
    assert.deepEqual(replies, ["\x1bP>|Resesh\x1b\\", "\x1bP>|Resesh\x1b\\"]);
    replies.length = 0;
    await write("\x1b[>1q");
    assert.deepEqual(replies, [], "other XTVERSION parameters are not ours to answer");
    await write("\x1b[?2026$p");
    assert.deepEqual(replies, ["\x1b[?2026;2$y"], "xterm.js reports synchronized output as supported");
    assert.equal(term.buffer.active.getLine(0).translateToString(true), "", "nothing is printed");
  } finally {
    term.dispose();
  }
});
