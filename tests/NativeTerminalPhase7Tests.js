"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");
const test = require("node:test");

const root = path.join(__dirname, "..");
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), "utf8");
const api = read("src", "Terminal", "NativeTerminalApi.cs");
const surface = read("src", "Terminal", "NativeTerminalSurface.cs");
const ruler = read("src", "Terminal", "NativeTerminalRuler.cs");
const patch = read("eng", "native-terminal-patches", "persistent-highlights.patch");
const manifest = JSON.parse(read("eng", "native-terminal.json"));
const capabilities = JSON.parse(read("eng", "native-terminal-capabilities.json"));

test("ABI 2.2 exports persistent multi-rule highlights", () => {
  assert.match(api, /AbiMajor\s*=\s*2/);
  assert.match(api, /AbiMinor\s*=\s*2/);
  assert.match(api, /ReseshTerminalSetHighlightRules/);
  assert.match(api, /ReseshTerminalClearHighlightRules/);
  assert.match(api, /ReseshTerminalGetHighlightRows/);
  assert.match(api, /record HighlightRulePayload/);
  assert.match(api, /record struct HighlightRowRecord/);
  assert.match(patch, /HRESULT __stdcall ReseshTerminalSetHighlightRules/);
  assert.match(patch, /HRESULT __stdcall ReseshTerminalClearHighlightRules/);
  assert.match(patch, /HRESULT __stdcall ReseshTerminalGetHighlightRows/);
});

test("decoration layer separates render colors from stored cell attributes", () => {
  assert.match(patch, /struct RenderDecoration/);
  assert.match(patch, /virtual std::span<const RenderDecoration> GetPersistentHighlights\(\) const noexcept/);
  assert.match(patch, /_drawPersistentHighlights/);
  assert.match(patch, /RETURN_IF_FAILED\(_drawPersistentHighlights\(_api\.persistentHighlights/);
  assert.match(patch, /RETURN_IF_FAILED\(_drawHighlighted\(_api\.searchHighlights/);
  assert.match(patch, /RETURN_IF_FAILED\(_drawHighlighted\(_api\.searchHighlightFocused/);
  assert.match(patch, /RETURN_IF_FAILED\(_drawHighlighted\(_api\.selectionSpans/);
  // Confirms stored cell attributes in TextBuffer are never mutated
  assert.doesNotMatch(patch, /textBuffer\.Write\(OutputCellIterator/);
  assert.doesNotMatch(patch, /dstRow\.Attributes\(\)/);
});

test("native surface and ruler integrate persistent highlights and overview ticks", () => {
  assert.match(surface, /_api\.SetHighlightRules\(_terminal,\s*_currentHighlightRules\)/);
  assert.match(surface, /_api\.ClearHighlightRules\(_terminal\)/);
  assert.match(surface, /var highlightRows = _api\.GetHighlightRows\(_terminal\)/);
  assert.match(surface, /_ruler\.UpdateAnnotations\(_marks,\s*searchRows,\s*highlightRows,\s*MarkLabel\)/);
  assert.match(ruler, /_highlightRows = highlightRows/);
  assert.match(ruler, /foreach \(var highlight in _highlightRows\)/);
  assert.match(ruler, /AddTick\(highlight\.Row,\s*lane:\s*2,\s*highlight\.Color/);
});

test("Phase 7 manifest and capability rows are complete and verified", () => {
  const capability = capabilities.capabilities.find((item) => item.capability === "persistent highlights");
  assert.ok(capability, "missing capability row: persistent highlights");
  assert.strictEqual(capability.native, "pass", "persistent highlights must pass after Phase 7");
  assert.match(capability.note, /Atlas.*precedence/);

  const patchEntry = manifest.fork.patches.find((p) => p.file === "native-terminal-patches/persistent-highlights.patch");
  assert.ok(patchEntry, "manifest must register persistent-highlights.patch");
  assert.strictEqual(manifest.abi.major, 2);
  assert.strictEqual(manifest.abi.minor, 2);
  assert.strictEqual(manifest.abi.buildId, "terminal-v1.24.11911.0-resesh-abi2.2-highlights");
});
