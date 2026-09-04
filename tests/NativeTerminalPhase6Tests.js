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
const patch = read("eng", "native-terminal-patches", "exact-snapshots.patch");
const capabilities = JSON.parse(read("eng", "native-terminal-capabilities.json"));

test("ABI 3 replaces interim ANSI snapshots with exact binary snapshots", () => {
  assert.match(api, /AbiMajor\s*=\s*3/);
  assert.match(api, /AbiMinor\s*=\s*1/);
  assert.match(api, /ReseshTerminalCaptureExactSnapshot/);
  assert.match(api, /ReseshTerminalRestoreExactSnapshot/);
  assert.doesNotMatch(api, /ReseshTerminalCaptureSnapshot/);
  assert.match(keyframe, /Magic = 0x504E5352/);
  assert.match(keyframe, /SchemaMajor = 1/);
  assert.match(keyframe, /Crc32\(fieldBytes\)/);
  assert.match(patch, /SnapshotMainBuffer/);
  assert.match(patch, /SnapshotAlternateBuffer/);
  assert.match(patch, /WriteDispatchState/);
  assert.match(patch, /ReseshTerminalRestoreExactSnapshot/);
  assert.match(patch, /RestorePendingSequence/);
  assert.doesNotMatch(surface, /resesh-native-keyframe-v1:/);
});

test("native live capture uses the 10-second or 1-MiB policy", () => {
  assert.match(surface, /KeyframeByteInterval = 1024 \* 1024/);
  assert.match(surface, /KeyframeTimeIntervalMilliseconds = 10_000/);
  assert.match(surface, /SupportsRewindCapture => true/);
  assert.match(surface, /CaptureNativeKeyframe\(force: true\)/);
  assert.match(surface, /_keyframeBytes < KeyframeByteInterval[\s\S]*?now - _lastKeyframeUnixMilliseconds < KeyframeTimeIntervalMilliseconds/);
  assert.match(surface, /_api\.CaptureExactSnapshot\(_terminal\)/);
  assert.match(surface, /KeyframeCaptured\?\.Invoke\(state, Columns, Rows, now\)/);
});

test("native rewind and asciicast playback seek from generated keyframes", () => {
  assert.match(factory, /CreatePlayback\(\) => new NativeTerminalSurface\(\)/);
  assert.match(player, /TerminalSurfaceFactory\.CreatePlayback\(\)/);
  assert.match(player, /_terminal\.LoadPlaybackAsync/);
  assert.match(player, /_terminal\.ShowReplayAsync/);
  assert.match(player, /_terminal\.SeekPlaybackAsync/);
  assert.match(surface, /LoadPlaybackAsync[\s\S]*?NativePlaybackFrame/);
  assert.match(surface, /item\.Time - lastFrameTime >= 10 \|\| bytesSinceFrame >= KeyframeByteInterval/);
  assert.match(surface, /for \(var index = frames\.Count - 1; index >= 0; index--\)/);
  assert.match(surface, /frames\[index\]\.Time <= _pendingPlaybackSeek/);
  assert.match(surface, /var generation = \+\+_replayGeneration/);
  assert.match(surface, /generation != _replayGeneration/);
  assert.match(surface, /_terminalPanel\.Opacity = 1;[\s\S]*?await SeekPlaybackAsync/);
  assert.match(surface, /ReplacePlaybackTerminal/);
  assert.match(surface, /_api\.ResizeCharacters/);
});

test("Phase 6 capability rows are complete", () => {
  for (const name of ["bounded instant rewind", "asciicast playback"]) {
    const capability = capabilities.capabilities.find((item) => item.capability === name);
    assert.ok(capability, `missing capability row: ${name}`);
    assert.strictEqual(capability.native, "pass", `${name} must pass after Phase 6`);
  }
});
