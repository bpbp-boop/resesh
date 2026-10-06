"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const page = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "wwwroot", "terminal.html"), "utf8");
const codecs = ["b64ToBytes", "strToB64"].map(name => {
  const match = page.match(new RegExp(String.raw`  function ${name}\([\s\S]*?\r?\n  }`));
  assert.ok(match, `${name} should exist`);
  return match[0];
}).join("\n");

function load(native) {
  const context = vm.createContext({ atob, btoa, TextEncoder });
  if (!native)
    vm.runInContext("delete Uint8Array.fromBase64; delete Uint8Array.prototype.toBase64;", context);
  vm.runInContext(codecs, context);
  return context;
}

// Large enough to cross the fallback's 32 KiB fromCharCode chunks, with multi-byte UTF-8.
const text = "héllo ✓ 👍 \x1b[31mred\x1b[0m\r\n".repeat(4000);

for (const native of [true, false]) {
  const skip = native && typeof Uint8Array.fromBase64 !== "function" && "this Node has no native base64";
  test(`host output and keyframe base64 round-trip (${native ? "native" : "fallback"} codecs)`, { skip }, () => {
    const { b64ToBytes, strToB64 } = load(native);
    const encoded = strToB64(text);
    assert.equal(encoded, Buffer.from(text, "utf8").toString("base64"));
    assert.deepEqual(Buffer.from(b64ToBytes(encoded)), Buffer.from(text, "utf8"));
    assert.equal(b64ToBytes("").length, 0);
  });
}
