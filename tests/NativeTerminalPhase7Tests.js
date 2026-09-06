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

test("ABI 3 retains persistent multi-rule highlights", () => {
  assert.match(api, /AbiMajor\s*=\s*3/);
  assert.match(api, /AbiMinor\s*=\s*1/);
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

test("suspended highlights do not force a redraw for every alternate-screen output chunk", () => {
  const suspendedBranch = patch.match(
    /if \(_highlightRules\.empty\(\) \|\| _terminal->IsAlternateBufferActive\(\)\)[\s\S]*?\n\+    \}/
  )?.[0] ?? "";
  const noState = suspendedBranch.indexOf(
    "if (_highlightSpans.empty() && _highlightRows.empty())"
  );
  const clear = suspendedBranch.indexOf("_highlightSpans.clear()");
  const redraw = suspendedBranch.indexOf("_renderer->TriggerRedrawAll()");
  assert.ok(noState >= 0, "unchanged suspended state must return before invalidating the renderer");
  assert.ok(clear > noState && redraw > clear,
    "the one required transition must still clear decorations and redraw");
});

test("streaming output coalesces highlight refresh without an idle delay", () => {
  const outputPath = patch.match(
    /void HwndTerminal::SendOutput\(std::wstring_view data\)[\s\S]*?\n }\n \n HwndTerminalSearchState HwndTerminal::Search/
  )?.[0] ?? "";
  assert.doesNotMatch(outputPath, /\n[ +]\s*_terminal->UpdatePatternsUnderLock\(\);/,
    "every output chunk must not synchronously rescan visible URLs and patterns");
  assert.doesNotMatch(outputPath, /_updateHighlights\(/,
    "every output chunk must not synchronously rescan the full scrollback");
  assert.match(outputPath, /_terminal->SetPersistentHighlights\(\{\}\)/,
    "stale decorations must be suspended while output is streaming");
  assert.match(patch,
    /HwndTerminal::GetHighlightRows\(\)[\s\S]*?UpdatePatternsUnderLock\(\)[\s\S]*?_updateHighlights\(false\)/,
    "the next annotation query must bring patterns and highlights current");
  assert.doesNotMatch(surface, /_annotationRefreshTimer/,
    "highlight refresh must not wait for output to become idle");
  assert.match(surface,
    /_api\.SendOutput\(_terminal, text\);\s*QueueAnnotationRefresh\(\);/,
    "text changes must refresh highlights even without a viewport event");
  assert.match(surface,
    /if \(_disposed \|\| _alternateBufferActive \|\| _annotationRefreshPending\)/,
    "repeated notifications must share one pending refresh");
  assert.match(surface,
    /_annotationRefreshPending = true;\s*if \(!DispatcherQueue\.TryEnqueue\(\(\) =>[\s\S]*?_annotationRefreshPending = false;[\s\S]*?RefreshAnnotations\(\)/,
    "refresh must run at normal priority before the next output block");
  assert.match(surface,
    /Interlocked\.Exchange\(ref _viewportRefreshPending, 1\) == 0[\s\S]*?TryEnqueue\(RefreshViewportFromNativeEvent\)/,
    "buffer notifications must collapse into one pending dispatcher update");
  assert.doesNotMatch(surface, /_outputFlushTimer/);
  assert.match(surface,
    /DispatcherQueuePriority\.Low, \(\) => FlushOutput\(\)/,
    "output must yield to input without a fixed frame delay");
  assert.match(surface, /_pendingOutput\.Read\(out byteCount\)/,
    "each UI update must consume a bounded output block");
  assert.match(surface,
    /_keyframeBytes[\s\S]*?QueueNativeKeyframeCapture\(\)/);
  assert.doesNotMatch(surface,
    /_keyframeBytes[\s\S]{0,120}?CaptureNativeKeyframe\(force: false\)/,
    "streaming output must not synchronously serialize the full scrollback");
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
  assert.strictEqual(manifest.abi.major, 3);
  assert.strictEqual(manifest.abi.minor, 1);
  assert.strictEqual(manifest.abi.buildId, "terminal-v1.24.11911.0-resesh-abi3.1-history-resize");
});
