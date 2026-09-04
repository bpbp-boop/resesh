"use strict";

const assert = require("assert");
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const test = require("node:test");

const root = path.join(__dirname, "..");
const read = (...parts) => fs.readFileSync(path.join(root, ...parts), "utf8");
const patch = read("eng", "native-terminal-patches", "exact-snapshots.patch");
const manifest = JSON.parse(read("eng", "native-terminal.json"));
const surface = read("src", "Terminal", "NativeTerminalSurface.cs");
const keyframe = read("src", "Terminal", "NativeTerminalKeyframe.cs");
const player = read("src", "App", "Terminal", "TerminalPlayerView.cs");
const html = read("src", "Terminal", "wwwroot", "terminal.html");

test("native snapshot patch contains every portable logical state group", () => {
  for (const token of [
    "SnapshotMagic", "SnapshotMajor", "SnapshotMinor", "SnapshotCrc32",
    "SnapshotMainBuffer", "SnapshotAlternateBuffer", "WriteBuffer",
    "GlyphAt", "DbcsAttrAt", "WriteAttribute", "SetLineRendition",
    "ReseshHyperlinkMap", "GetCustomIdFromId", "SetScrollbarData",
    "CursorDelayedWrap", "ReseshMutableViewport", "ReseshUserScrollOffset",
    "ReseshSystemModes", "ReseshInputModes", "IsAlternateBufferActive",
    "GetColorTable", "GetColorAliasIndex", "WriteDispatchState",
    "TabStops", "ScrollMargins", "SavedCursors", "CharacterSets",
    "ReseshPendingSequence", "RestorePendingSequence", "Title", "WorkingDirectory",
  ]) {
    assert.match(patch, new RegExp(token), `exact snapshot patch must contain ${token}`);
  }
  assert.match(patch, /MaximumSnapshotBytes = 32 \* 1024 \* 1024/);
  assert.match(patch, /case SnapshotDispatchState:[\s\S]*?ReadDispatchState/);
  assert.match(patch, /default:[\s\S]*?break;/, "unknown minor fields must be skipped by length");
});

test("managed envelope is bounded, checksummed, and carries rebuildable state", () => {
  for (const token of [
    "MaximumEnvelopeLength", "SchemaMajor", "SchemaMinor", "featureFlags",
    "Crc32", "PendingUtf8Field", "SearchField", "HighlightRulesField",
    "SnapshotUtf8Decoder.IsValidPending", "unsupported feature flags",
  ]) {
    assert.match(keyframe, new RegExp(token));
  }
  assert.match(surface, /_outputDecoder\.CapturePending\(\)/);
  assert.match(surface, /NativeTerminalSnapshotCodec\.Decode/);
  assert.match(surface, /_api\.RestoreExactSnapshot\(candidate, envelope\.NativeSnapshot\)/);
});

test("restore validates a detached read-only candidate before renderer attachment", () => {
  const create = surface.indexOf("var candidate = _api.CreateTerminal");
  const restore = surface.indexOf("_api.RestoreExactSnapshot(candidate");
  const publish = surface.indexOf("_terminal = candidate", restore);
  const callback = surface.indexOf("_api.RegisterEventCallback(candidate", publish);
  const attach = surface.indexOf("AttachSwapChainPanel", publish);
  assert.ok(create >= 0 && create < restore && restore < publish && publish < attach && attach < callback);
  assert.match(surface, /ReadOnly: true/);
  assert.match(surface, /catch[\s\S]*?_api\.DestroyTerminal\(candidate\);[\s\S]*?throw;/);
  assert.match(player, /private readonly NativeTerminalSurface _terminal/);
});

test("ANSI and xterm serializer playback paths are absent after cutover", () => {
  assert.ok(!fs.existsSync(path.join(root, "src", "Terminal", "wwwroot", "addon-serialize.js")));
  assert.doesNotMatch(html, /SerializeAddon|loadPlayback|seekPlayback|showReplay/);
  assert.doesNotMatch(surface, /CaptureSnapshot|serializer|resesh-native-keyframe-v1/);
});

test("ABI 3 snapshot patch pin is normalized and exact", () => {
  const entry = manifest.fork.patches.find((item) => item.file === "native-terminal-patches/exact-snapshots.patch");
  assert.ok(entry);
  const actual = crypto.createHash("sha256").update(patch.replace(/\r\n?/g, "\n"), "utf8").digest("hex");
  assert.strictEqual(entry.sha256, actual);
  assert.deepStrictEqual(manifest.abi, {
    major: 3,
    minor: 1,
    buildId: "terminal-v1.24.11911.0-resesh-abi3.1-history-resize",
  });
});
