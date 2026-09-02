"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");

const api = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "NativeTerminalApi.cs"),
  "utf8"
);
const surface = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "NativeTerminalSurface.cs"),
  "utf8"
);
const capabilities = JSON.parse(fs.readFileSync(
  path.join(__dirname, "..", "eng", "native-terminal-capabilities.json"),
  "utf8"
));

assert.match(api, /AbiMinor\s*=\s*3/);
for (const eventName of [
  "TitleChanged",
  "WorkingDirectoryChanged",
  "Bell",
  "BufferOrViewportChanged",
  "AlternateBufferChanged",
  "ShellIntegrationMarkChanged",
  "TerminalModeChanged",
  "OscObserved",
  "OpenLink",
]) {
  assert.match(api, new RegExp(`${eventName}\\s*=\\s*\\d+`), `${eventName} must have a typed ABI value`);
}
assert.match(api, /internal long Value0;/);
assert.match(api, /internal long Value1;/);
assert.match(api, /internal long Value2;/);

assert.doesNotMatch(surface, /data\.IndexOf\(\(byte\)0x07\)/,
  "a BEL OSC terminator must not become a bell event");
assert.match(surface, /eventData\.Sequence\s*<=\s*_lastNativeEventSequence/,
  "native events must be monotonic");
assert.match(surface, /case NativeTerminalApi\.NativeEventType\.OscObserved:[\s\S]*?ObserveOsc\(/);
assert.match(surface, /case 7 when IsValidOscPayload\(payload, 2048\)/);
assert.match(surface, /case 133 when IsValidOscPayload\(payload, 4096\)/);
assert.match(surface, /case 3008 when IsValidOscPayload\(payload, 4096\)/);
assert.match(surface, /case 7377 or 9 or 777 when IsValidOscPayload\(payload, 2048\)/);
assert.match(surface, /new UTF8Encoding\(false, false\)\.GetDecoder\(\)/,
  "one persistent decoder must preserve UTF-8 split across backend reads");
assert.match(surface, /_outputDecoder\.GetChars\([\s\S]*?flush:\s*false\)/);
assert.match(surface, /DispatcherQueue\.TryEnqueue\(\(\) => TitleChanged\?\.Invoke/,
  "native callbacks must queue application state changes");
assert.match(surface, /DispatcherQueue\.TryEnqueue\(\(\) => WorkingDirectoryReported\?\.Invoke/);
assert.match(surface, /DispatcherQueue\.TryEnqueue\(\(\) => AgentOscReceived\?\.Invoke/);
assert.match(api, /ReseshTerminalSearch/);
assert.match(api, /ReseshTerminalClearSearch/);
assert.match(api, /ReseshTerminalGetSearchState/);
assert.match(surface, /NativeTerminalFindInput/);
assert.match(surface, /case NativeTerminalApi\.NativeEventType\.OpenLink:[\s\S]*?TerminalLinkPolicy\.Open/);
assert.match(surface, /new Windows\.Foundation\.Rect\(0, findHeight, ActualWidth/,
  "the find row must reduce the child HWND bounds");

for (const name of [
  "OSC 7 working-directory event",
  "OSC 133 shell marks",
  "OSC 3008 context event",
  "OSC 7377, 9, and 777 agent evidence",
  "OSC 8 link rendering",
  "search",
]) {
  const item = capabilities.capabilities.find((capability) => capability.capability === name);
  assert.ok(item, `missing capability row: ${name}`);
  assert.strictEqual(item.native, "pass", `${name} must pass after Phase 4`);
}

console.log("Native terminal event tests passed.");
