const assert = require("node:assert/strict");
const test = require("node:test");

const source = require("./support/mainWindowSource");

const method = (pattern) => source.match(pattern)?.[0] ?? "";

// The palette and history overlays each capture where focus returns when they open, and
// release it through one close path, so closing one for the other, or reopening one while a
// command from it still runs, can't lose or reuse the other's focus target.
test("each overlay closes only through its own close path", () => {
  // Nothing outside the close paths hides an overlay directly.
  const direct = [...source.matchAll(/(CommandPalette|HistoryOverlay)\.Close\(\)/g)];
  assert.equal(direct.length, 2);
  const palette = method(/private OverlayFocusSession\? CloseCommandPalette\(bool restoreFocus\)[\s\S]*?\n    }\r?\n/);
  assert.match(palette, /CommandPalette\.Close\(\);\s*var focus = _paletteFocus;\s*_paletteFocus = null;\s*if \(restoreFocus\)\s*RestoreOverlayFocus\(focus\);/);
  const history = method(/private OverlayFocusSession\? CloseHistory\(bool restoreFocus\)[\s\S]*?\n    }\r?\n/);
  assert.match(history, /HistoryOverlay\.Close\(\);\s*var focus = _historyFocus;\s*_historyFocus = null;\s*if \(restoreFocus\)\s*RestoreOverlayFocus\(focus\);/);

  assert.match(source, /CommandPalette\.CloseRequested \+= \(\) => CloseCommandPalette\(restoreFocus: true\)/);
  assert.match(source, /HistoryOverlay\.CloseRequested \+= \(\) => CloseHistory\(restoreFocus: true\)/);
});

test("an overlay that replaces the other inherits its focus session", () => {
  const showPalette = method(/private void ShowCommandPalette\(\)[\s\S]*?\n    }\r?\n/);
  assert.match(showPalette, /CloseHistory\(restoreFocus: false\) \?\? CaptureOverlayFocus\(\)/);
  const showHistory = method(/private void ShowHistory\([\s\S]*?\n    }\r?\n/);
  assert.match(showHistory, /CloseHistory\(restoreFocus: false\)\s*\?\? CloseCommandPalette\(restoreFocus: false\)\s*\?\? CaptureOverlayFocus\(\)/);
});

test("a palette command keeps its own focus session and never clears a reopened palette's", () => {
  const execute = method(/private async Task ExecuteCommandPaletteEntryAsync[\s\S]*?\n    }\r?\n/);
  assert.match(execute, /var focus = CloseCommandPalette\(restoreFocus: false\);/);
  assert.match(execute, /RunWithInvocationFocus\(focus, command\.ExecuteAsync\)/);
  assert.doesNotMatch(execute, /_paletteFocus|_historyFocus/);
  assert.match(execute, /finally[\s\S]*?!CommandPalette\.IsOpen && !HistoryOverlay\.IsOpen\)\s*FocusActiveTerminal\(\)/);

  // The invocation context is restored, not just cleared, so nested invocations unwind.
  const run = method(/private T RunWithInvocationFocus<T>[\s\S]*?\n    }\r?\n/);
  assert.match(run, /var outer = _invocationFocus;[\s\S]*?finally\s*\{\s*_invocationFocus = outer;/);
  assert.doesNotMatch(source, /_invokedFromTerminal/);
});
