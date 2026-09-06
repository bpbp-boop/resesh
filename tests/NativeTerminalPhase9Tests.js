"use strict";

const assert = require("assert");
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const test = require("node:test");

const root = path.join(__dirname, "..");
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), "utf8");
const patch = read("eng", "native-terminal-patches", "live-history-resize.patch");
const manifest = JSON.parse(read("eng", "native-terminal.json"));
const capabilities = JSON.parse(read("eng", "native-terminal-capabilities.json"));
const api = read("src", "Terminal", "NativeTerminalApi.cs");
const surface = read("src", "Terminal", "NativeTerminalSurface.cs");

test("native patch implements atomic live history resizing", () => {
  for (const token of [
    "TextBuffer::ResizeHeight",
    "Terminal::SetHistorySize",
    "HwndTerminal::SetHistorySize",
    "ReseshTerminalOptionHistorySize",
    "rowsToDrop",
    "rowsRemoved",
    "_scrollOffset",
    "_mutableViewport",
    "_markGeneration",
    "_searcher.Reset",
    "_updateHighlights(true)",
    "_NotifyScrollEvent()",
  ]) {
    assert.match(patch, new RegExp(token.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")), `missing ${token}`);
  }
  assert.match(patch, /if \(!_inAltBuffer\(\)\)[\s\S]*?_mainBuffer->TriggerRedrawAll/);
  assert.match(patch, /newVisibleTop = std::clamp\(oldVisibleTop - removed, 0, newMutableTop\)/);
});

test("native behavioral coverage includes every Phase 9 edge", () => {
  for (const evidence of [
    "eventsBeforeShrink",
    "retainedBookmark",
    "requiredSearchRows",
    "requiredHighlightRows",
    "beforeResizeSnapshot",
    "afterResizeSnapshot",
    "enterAlternate",
    "outputThread",
    "ERROR_REVISION_MISMATCH",
  ]) {
    assert.match(patch, new RegExp(evidence));
  }
  assert.match(patch, /bufferEventsBeforeShrink \+ 1/);
  assert.match(patch, /https:\/\/example\.test\/retained/);
});

test("managed settings updates use ABI 3.1 history options without recreating the surface", () => {
  assert.match(api, /AbiMinor = 1/);
  assert.match(api, /HistorySizeOption = 0x00000004/);
  assert.match(api, /internal void SetHistorySize\(IntPtr terminal, int historySize\)/);
  assert.match(api, /Flags = HistorySizeOption[\s\S]*?HistorySize = historySize/);
  const applyOptions = surface.match(/public override void ApplyOptions\([\s\S]*?\r?\n    \}/)?.[0] ?? "";
  assert.match(applyOptions, /scrollbackChanged[\s\S]*?_api\.SetHistorySize\(_terminal, scrollback!\.Value\)/);
  assert.doesNotMatch(applyOptions, /CreateTerminal/);
});

test("ABI 3.1 history patch pin is normalized and exact", () => {
  const entry = manifest.fork.patches.find((item) => item.file === "native-terminal-patches/live-history-resize.patch");
  assert.ok(entry);
  const actual = crypto.createHash("sha256").update(patch.replace(/\r\n?/g, "\n"), "utf8").digest("hex");
  assert.strictEqual(entry.sha256, actual);
  assert.deepStrictEqual(manifest.abi, {
    major: 3,
    minor: 1,
    buildId: "terminal-v1.24.11911.0-resesh-abi3.1-history-resize",
  });

  const capability = capabilities.capabilities.find((item) => item.capability === "live scrollback resizing");
  assert.ok(capability);
  assert.strictEqual(capability.native, "pass");
});
