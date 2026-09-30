const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const source = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "wwwroot", "addon-ruler.js"),
  "utf8");
const window = { devicePixelRatio: 1 };
const timers = [];
vm.runInNewContext(source, {
  window, Map, Math, RegExp, Set, requestAnimationFrame() {},
  setTimeout(fn, ms) { timers.push({ fn, ms }); return timers.length; },
  clearTimeout() {},
});
const RulerAddon = window.RulerAddon.RulerAddon;

function paintPresentation(isSplit, isGroupFocused, isPointerOver = false, configure = () => {}) {
  const operations = [];
  const context = {
    fillStyle: "",
    globalAlpha: 1,
    clearRect() {},
    fillRect(x, y, width, height) {
      operations.push({ x, y, width, height, color: this.fillStyle, alpha: this.globalAlpha });
    },
  };
  const addon = new RulerAddon();
  addon._term = {
    rows: 20,
    buffer: { active: { type: "normal", length: 100, viewportY: 50 } },
  };
  addon._strip = { clientHeight: 100, style: {}, dataset: {} };
  addon._canvas = { width: 0, height: 0, getContext: () => context };
  addon._thumb = { style: {} };
  addon._cmdMarks = [
    { marker: { line: 10 }, exit: 0 },
    { marker: { line: 11 }, exit: 0 },
    { marker: { line: 20 }, exit: 2 },
  ];
  addon._bookmarks = [{ marker: { line: 30 } }];
  addon.setPresentation(isSplit, isGroupFocused);
  addon._isPointerOver = isPointerOver;
  configure(addon);
  addon._paint();
  return {
    operations,
    presentation: addon._strip.dataset.presentation,
    thumbStyle: addon._thumb.style,
  };
}

test("dense overview ticks preserve composited colors without drawing every hit", () => {
  const result = paintPresentation(false, true, false, addon => {
    addon._term.buffer.active.length = 10000;
    addon._hlRules = [{ color: "#ff0000" }, { color: "#0000ff" }];
    addon._hlAnchor = { virtual: 0, marker: { line: 0 } };
    // These all project to the same pixel row. Preserve both density and the
    // ordering of differently colored runs when combining duplicate rectangles.
    addon._hlIndex = new Map([[0, 1], [1, 1], [2, 2], [3, 1], [4, 1]]);
    addon._hlFrontier = 10000;
  });
  const ticks = result.operations.filter(op => op.color === "#ff0000" || op.color === "#0000ff");
  assert.deepEqual(ticks.map(op => [op.y, op.color]), [
    [0, "#ff0000"], [0, "#0000ff"], [0, "#ff0000"],
  ]);
  const composite = operations => operations.reduce((pixel, op) => {
    const color = op.color === "#ff0000" ? [1, 0, 0] : [0, 0, 1];
    return pixel.map((value, channel) => value * (1 - op.alpha) + color[channel] * op.alpha);
  }, [0, 0, 0]);
  const expected = composite(["#ff0000", "#ff0000", "#0000ff", "#ff0000", "#ff0000"]
    .map(color => ({ color, alpha: 0.8 })));
  const actual = composite(ticks);
  actual.forEach((value, channel) => assert.ok(Math.abs(value - expected[channel]) < 1e-12));
});

test("timed-out idle passes make bounded progress and eventually index busy output", () => {
  const addon = new RulerAddon();
  const lineCount = 10000;
  addon._term = {
    buffer: { active: {
      type: "normal", baseY: 0, cursorY: lineCount,
      getLine: () => ({ translateToString: () => "error" }),
    } },
    registerMarker: () => ({ line: lineCount, isDisposed: false, onDispose() {}, dispose() {} }),
  };
  addon.setHighlightRules([{ id: "error", pattern: "error", showInOverview: true }]);
  const timeout = { didTimeout: true, timeRemaining: () => 0 };
  addon._hlScan(timeout);
  assert.ok(addon._hlIndex.has(0), "a timeout must not starve the first completed line");
  assert.ok(!addon._hlIndex.has(lineCount - 1), "a timeout must not process unbounded work");
  for (let i = 0; i < 10; i++) addon._hlScan(timeout);
  assert.equal(addon._hlIndex.size, lineCount);
});

function operationsWithColor(result, color) {
  return result.operations.filter(operation => operation.color === color);
}

test("single-pane ruler keeps the full presentation", () => {
  const result = paintPresentation(false, true);

  assert.equal(result.presentation, "full");
  assert.deepEqual(result.operations[0],
    { x: 0, y: 0, width: 14, height: 100, color: "#0c0c0c", alpha: 1 });
  assert.equal(operationsWithColor(result, "#2ea043")[0].alpha, 1);
});

test("focused split ruler keeps full width, uses the Calm hierarchy, and merges marks", () => {
  const result = paintPresentation(true, true);
  const routine = operationsWithColor(result, "#9e9e9e");

  assert.equal(result.presentation, "calm-focused");
  assert.deepEqual(result.operations[0],
    { x: 0, y: 0, width: 14, height: 100, color: "#0c0c0c", alpha: 1 });
  assert.equal(routine.length, 2);
  assert.equal(routine[0].alpha, 0.42);
  assert.equal(routine[0].y, routine[1].y);
  assert.equal(operationsWithColor(result, "#ff5555")[0].alpha, 0.90);
  assert.equal(operationsWithColor(result, "#61d6d6")[0].alpha, 0.90);
});

test("inactive split ruler dims important and routine marks further", () => {
  const result = paintPresentation(true, false);

  assert.equal(result.presentation, "calm-unfocused");
  assert.equal(operationsWithColor(result, "#9e9e9e")[0].alpha, 0.25);
  assert.equal(operationsWithColor(result, "#ff5555")[0].alpha, 0.62);
  assert.equal(operationsWithColor(result, "#61d6d6")[0].alpha, 0.62);
});

test("hover keeps the full width and restores mark colors and opacity", () => {
  const result = paintPresentation(true, false, true);

  assert.equal(result.presentation, "full");
  assert.equal(result.operations[0].x, 0);
  assert.equal(result.operations[0].width, 14);
  assert.equal(operationsWithColor(result, "#2ea043")[0].alpha, 1);
});

test("ruler thumb tracks the viewport independently from annotated marks", () => {
  const result = paintPresentation(false, true);

  assert.equal(result.thumbStyle.top, "50px");
  assert.equal(result.thumbStyle.height, "20px");
  assert.equal(operationsWithColor(result, "rgba(121,121,121,0.40)").length, 0);
});

test("ruler thumb uses VS Code reveal and fade timing", () => {
  const addon = new RulerAddon();
  addon._thumb = { style: {} };

  addon._syncThumbVisibility();
  assert.equal(addon._thumb.style.opacity, "0");
  assert.equal(addon._thumb.style.transitionDuration, "800ms");

  addon._isPointerOver = true;
  addon._syncThumbVisibility();
  assert.equal(addon._thumb.style.opacity, "1");
  assert.equal(addon._thumb.style.transitionDuration, "100ms");

  addon._isPointerOver = false;
  addon._drag = { pointerId: 7 };
  addon._syncThumbVisibility();
  assert.equal(addon._thumb.style.opacity, "1");
});

function dragHarness() {
  const released = [];
  const scrolled = [];
  const addon = new RulerAddon();
  addon._strip = {
    clientHeight: 100,
    releasePointerCapture(pointerId) { released.push(pointerId); },
  };
  addon._term = {
    rows: 20,
    buffer: { active: { length: 100 } },
    scrollToLine(line) { scrolled.push(line); },
  };
  addon._drag = { pointerId: 7, startY: 10, moved: false };
  return { addon, released, scrolled };
}

test("ruler cancels a stale drag when the primary button is no longer down", () => {
  const { addon, released, scrolled } = dragHarness();

  addon._onPointerMove({ pointerId: 7, buttons: 0, offsetY: 80 });

  assert.equal(addon._drag, null);
  assert.deepEqual(released, [7]);
  assert.deepEqual(scrolled, []);
});

test("ruler cancellation ignores a different pointer", () => {
  const { addon, released } = dragHarness();

  addon._cancelDrag(8);

  assert.notEqual(addon._drag, null);
  assert.deepEqual(released, []);
});

function scrollHarness({ viewportY, baseY }) {
  const marker = { line: baseY, isDisposed: false };
  const addon = new RulerAddon();
  addon._thumb = { style: {} };
  addon._term = {
    buffer: { active: { type: "normal", viewportY, baseY } },
    registerMarker() { return marker; },
  };
  addon._onViewportScroll(); // baseline
  timers.length = 0;
  const scroll = (next, trimmed = 0) => {
    Object.assign(addon._term.buffer.active, next);
    marker.line -= trimmed;
    addon._onViewportScroll();
  };
  return { addon, scroll };
}

test("a user scroll reveals the thumb, then it fades after a short linger", () => {
  const { addon, scroll } = scrollHarness({ viewportY: 80, baseY: 80 });

  scroll({ viewportY: 77 }); // wheel up three rows
  assert.equal(addon._thumb.style.opacity, "1");
  assert.equal(timers.length, 1);
  assert.equal(timers[0].ms, 500);

  timers[0].fn();
  assert.equal(addon._thumb.style.opacity, "0");
  assert.equal(addon._thumb.style.transitionDuration, "800ms");
});

test("output following a bottom-pinned view does not reveal the thumb", () => {
  const { addon, scroll } = scrollHarness({ viewportY: 80, baseY: 80 });

  scroll({ viewportY: 81, baseY: 81 });
  scroll({ viewportY: 90, baseY: 90 });
  assert.notEqual(addon._thumb.style.opacity, "1");
  assert.equal(timers.length, 0);
});

test("scrollback trimming under a scrolled-back view does not reveal the thumb", () => {
  const { addon, scroll } = scrollHarness({ viewportY: 40, baseY: 80 });

  scroll({ viewportY: 39 }, 1); // full scrollback: top line trimmed, view kept still
  assert.equal(timers.length, 0);

  scroll({ viewportY: 38 }, 0); // a real one-row scroll up
  assert.equal(timers.length, 1);
});

test("dragging the thumb keeps the grab point under the pointer", () => {
  const scrolled = [];
  const addon = new RulerAddon();
  addon._strip = { clientHeight: 100, setPointerCapture() {} };
  addon._term = {
    rows: 20,
    buffer: { active: { length: 100 } },
    scrollToLine(line) { scrolled.push(line); },
  };
  addon._thumbTop = 40;
  addon._thumbHeight = 20;

  // Grab near the thumb's top edge; a centring scrub would jump 8 px here.
  addon._onPointerDown({ button: 0, pointerId: 1, offsetY: 42, preventDefault() {} });
  addon._onPointerMove({ pointerId: 1, buttons: 1, offsetY: 52 });
  // Thumb top moves 40 -> 50 of an 80 px track over 80 scrollable rows.
  assert.deepEqual(scrolled, [50]);
});

test("clicking the thumb without dragging does not scroll", () => {
  const scrolled = [];
  const addon = new RulerAddon();
  addon._strip = { clientHeight: 100, setPointerCapture() {}, releasePointerCapture() {} };
  addon._term = {
    rows: 20,
    buffer: { active: { length: 100 } },
    scrollToLine(line) { scrolled.push(line); },
    focus() {},
  };
  addon._thumbTop = 40;
  addon._thumbHeight = 20;

  addon._onPointerDown({ button: 0, pointerId: 1, offsetY: 50, preventDefault() {} });
  addon._onPointerUp({ pointerId: 1, offsetY: 50 });
  assert.deepEqual(scrolled, []);
});

test("thumb meets both track ends and hides when nothing can scroll", () => {
  const atBottom = paintPresentation(false, true, false, addon => {
    addon._term.buffer.active.length = 10000;
    addon._term.buffer.active.viewportY = 9980;
  });
  assert.equal(atBottom.thumbStyle.height, "20px");
  assert.equal(atBottom.thumbStyle.top, "80px");

  const fits = paintPresentation(false, true, false, addon => {
    addon._term.buffer.active.length = 20;
    addon._term.buffer.active.viewportY = 0;
  });
  assert.equal(fits.thumbStyle.display, "none");
});

test("wheel over the ruler accumulates touchpad travel by row", () => {
  const scrolledLines = [];
  const addon = new RulerAddon();
  addon._term = {
    rows: 10,
    options: {},
    element: { querySelector: () => ({ clientHeight: 200 }) }, // 20 px rows
    scrollLines(lines) { scrolledLines.push(lines); },
  };
  const wheel = deltaY => addon._onWheel({ deltaY, deltaMode: 0, preventDefault() {} });

  // 8 px of wheel is 10 px of scroll: half a row each, never a whole row per event.
  wheel(8);
  assert.deepEqual(scrolledLines, []);
  wheel(8);
  assert.deepEqual(scrolledLines, [1]);

  // A mouse notch (wheelDeltaY 120) is 50 px: two rows, with half a row carried.
  scrolledLines.length = 0;
  addon._wheelRemainder = 0;
  addon._onWheel({ deltaY: 100, wheelDeltaY: -120, deltaMode: 0, preventDefault() {} });
  assert.deepEqual(scrolledLines, [2]);
});

function timestampHarness(lines, cursorLine) {
  const disposeHandlers = [];
  const marker = {
    line: cursorLine,
    dispose() {},
    onDispose(handler) { disposeHandlers.push(handler); },
  };
  const addon = new RulerAddon();
  addon._term = {
    buffer: {
      active: {
        type: "normal",
        baseY: cursorLine,
        cursorY: 0,
        get length() { return lines.length; },
        getLine(index) { return lines[index] || null; },
      },
    },
    registerMarker() { marker.line = addon._term.buffer.active.baseY; return marker; },
  };
  return { addon, marker };
}

test("line times follow wrapped rows and scrollback trimming", () => {
  const lines = [{ isWrapped: false }, { isWrapped: false }, { isWrapped: true }];
  const { addon, marker } = timestampHarness(lines, 2);
  const when = Date.UTC(2026, 7, 16, 4, 32);

  addon._timeStampLine(1, when);
  assert.equal(addon._timeForLine(2), when);

  // The logical line and its continuation each moved up by one row. The sentinel
  // marker moved with them, so the virtual coordinate still resolves the same time.
  lines.shift();
  marker.line--;
  addon._term.buffer.active.baseY--;
  assert.equal(addon._timeForLine(1), when);
});

test("timestamped writes stay ordered while xterm parses asynchronously", () => {
  const lines = [{ isWrapped: false }];
  const { addon } = timestampHarness(lines, 0);
  const writes = [];
  addon._term.write = (data, done) => writes.push({ data, done });

  addon.writeOutput("first", 1000);
  addon.writeOutput("second", 2000);
  assert.equal(writes.length, 1);
  assert.equal(writes[0].data, "first");
  assert.equal(addon._timeForLine(0), 1000);

  writes[0].done();
  assert.equal(writes.length, 2);
  assert.equal(writes[1].data, "second");
  assert.equal(addon._timeForLine(0), 2000);
});

test("line times are restored by logical-line order after reflow", () => {
  const lines = [{ isWrapped: false }, { isWrapped: true }, { isWrapped: false }];
  const { addon } = timestampHarness(lines, 2);
  addon._timeStampLine(0, 1000);
  addon._timeStampLine(2, 2000);
  addon.captureTimestampReflow();

  // A wider terminal unwraps the first logical line and wraps the second one.
  lines.splice(0, lines.length,
    { isWrapped: false }, { isWrapped: false }, { isWrapped: true });
  addon.restoreTimestampReflow();

  assert.equal(addon._timeForLine(0), 1000);
  assert.equal(addon._timeForLine(2), 2000);
});

test("timestamp text is coarse local wall clock plus relative age", () => {
  const addon = new RulerAddon();
  const when = new Date(2026, 7, 16, 14, 32).getTime();

  const old = addon._formatTimestamp(when, when + 3 * 60 * 60 * 1000);
  assert.equal(old.clock, "14:32");
  assert.equal(old.relative, "3h ago");

  const recent = addon._formatTimestamp(when, when + 45 * 1000);
  assert.equal(recent.clock, "14:32");
  assert.equal(recent.relative, "now");
});

test("command tooltip includes its wall clock and relative age", () => {
  const lines = [{
    isWrapped: false,
    translateToString() { return "$ deploy"; },
  }];
  const { addon } = timestampHarness(lines, 0);
  addon._strip = { clientHeight: 100 };
  addon._tooltip = { textContent: "", offsetHeight: 20, style: {} };
  addon._cmdMarks = [{ marker: { line: 0 }, exit: 2, src: "osc" }];
  addon._timeStampLine(0, Date.now() - 3 * 60 * 60 * 1000);

  addon._showTooltip(0);

  assert.match(addon._tooltip.textContent, /^\$ deploy\nexit 2 · \d{2}:\d{2} · 3h ago$/);
});

test("command card colors the prompt and alert metadata separately", () => {
  function makeElement(ownerDocument) {
    return {
      ownerDocument,
      children: [],
      style: {},
      textContent: "",
      appendChild(child) { this.children.push(child); return child; },
    };
  }
  const doc = {
    createElement() { return makeElement(doc); },
    createTextNode(text) { return { textContent: text }; },
  };
  const tip = makeElement(doc);
  const addon = new RulerAddon();

  addon._renderTooltip(tip, "$ deploy", [
    { text: "exit 2", color: null },
    { text: "61 errors below", color: addon._colors.cmdFail },
  ]);

  assert.equal(tip.children.length, 2);
  assert.equal(tip.children[0].children[0].textContent, "$");
  assert.equal(tip.children[0].children[0].style.color, addon._colors.cmdOk);
  assert.equal(tip.children[0].children[1].textContent, " deploy");
  assert.equal(tip.children[1].children[0].style.color, undefined);
  assert.equal(tip.children[1].children[2].textContent, "61 errors below");
  assert.equal(tip.children[1].children[2].style.color, addon._colors.cmdFail);
});
