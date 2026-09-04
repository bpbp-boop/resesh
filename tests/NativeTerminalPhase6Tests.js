"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");
const test = require("node:test");

const root = path.join(__dirname, "..");
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), "utf8");
const api = read("src", "Terminal", "NativeTerminalApi.cs");
const surface = read("src", "Terminal", "NativeTerminalSurface.cs");
const keyframe = read("src", "Terminal", "NativeTerminalKeyframe.cs");
const player = read("src", "App", "Terminal", "TerminalPlayerView.cs");
const factory = read("src", "Terminal", "TerminalSurface.cs");
const patch = read("eng", "native-terminal-patches", "ansi-keyframes.patch");
const capabilities = JSON.parse(read("eng", "native-terminal-capabilities.json"));

test("ABI 2.1 exports versioned in-memory ANSI snapshots", () => {
  assert.match(api, /AbiMajor\s*=\s*2/);
  assert.match(api, /AbiMinor\s*=\s*[1-9]/);
  assert.match(api, /ReseshTerminalCaptureSnapshot/);
  assert.match(api, /ReseshTerminalResizeCharacters/);
  assert.match(api, /record struct Snapshot\([\s\S]*?CaptureSequence[\s\S]*?UnixTimeMilliseconds[\s\S]*?Ansi[\s\S]*?WorkingDirectory/);
  assert.match(api, /ValidateSnapshot\(in snapshot, written\)/);
  assert.match(keyframe, /resesh-native-keyframe-v1:/);
  assert.match(keyframe, /SchemaVersion[\s\S]*?BuildId[\s\S]*?ViewportTop[\s\S]*?AlternateBuffer[\s\S]*?CaptureSequence/);
  assert.match(patch, /void TextBuffer::SerializeTo\(std::wstring& destination\) const/);
  assert.match(patch, /CopyRow\(row, row, \*bufferCopy\)/);
  assert.match(patch, /snapshot\.Size = _terminal->GetViewport\(\)\.Dimensions\(\)/);
  assert.match(patch, /bufferCopy->SerializeTo\(snapshot\.Ansi\)/);
  assert.match(patch, /if \(code == 7\)[\s\S]*?SetWorkingDirectory\(payload\)/);
  assert.match(patch, /ReseshTerminalCaptureSnapshot/);
});

test("native live capture uses the 10-second or 1-MiB policy", () => {
  assert.match(surface, /KeyframeByteInterval = 1024 \* 1024/);
  assert.match(surface, /KeyframeTimeIntervalMilliseconds = 10_000/);
  assert.match(surface, /SupportsRewindCapture => true/);
  assert.match(surface, /CaptureNativeKeyframe\(force: true\)/);
  assert.match(surface, /_keyframeBytes < KeyframeByteInterval[\s\S]*?now - _lastKeyframeUnixMilliseconds < KeyframeTimeIntervalMilliseconds/);
  assert.match(surface, /_api\.CaptureSnapshot\(_terminal\)/);
  assert.match(surface, /KeyframeCaptured\?\.Invoke\(state, snapshot\.Columns, snapshot\.Rows, snapshot\.UnixTimeMilliseconds\)/);
});

test("native rewind and asciicast playback seek from generated keyframes", () => {
  assert.match(factory, /CreatePlayback\(\) => CreateLive\(\)/);
  assert.match(player, /TerminalSurfaceFactory\.CreatePlayback\(\)/);
  assert.match(player, /_terminal is NativeTerminalSurface native[\s\S]*?native\.LoadPlaybackAsync/);
  assert.match(player, /native\.ShowReplayAsync/);
  assert.match(player, /native\.SeekPlaybackAsync/);
  assert.match(surface, /LoadPlaybackAsync[\s\S]*?NativePlaybackFrame/);
  assert.match(surface, /item\.Time - lastFrameTime >= 10 \|\| bytesSinceFrame >= KeyframeByteInterval/);
  assert.match(surface, /for \(var index = frames\.Count - 1; index >= 0; index--\)/);
  assert.match(surface, /frames\[index\]\.Time <= _pendingPlaybackSeek/);
  assert.match(surface, /var generation = \+\+_replayGeneration/);
  assert.match(surface, /generation != _replayGeneration/);
  assert.match(surface, /_terminalPanel\.Opacity = 1;[\s\S]*?await SeekPlaybackAsync/);
  assert.match(surface, /ResetPlaybackTerminal/);
  assert.match(surface, /_api\.ResizeCharacters/);
});

test("Phase 6 capability rows are complete", () => {
  for (const name of ["bounded instant rewind", "asciicast playback"]) {
    const capability = capabilities.capabilities.find((item) => item.capability === name);
    assert.ok(capability, `missing capability row: ${name}`);
    assert.strictEqual(capability.native, "pass", `${name} must pass after Phase 6`);
  }
});
