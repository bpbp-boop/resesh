const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const source = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "wwwroot", "addon-ruler.js"),
  "utf8");
const window = { devicePixelRatio: 1 };
vm.runInNewContext(source, { window, Map, Math, RegExp, Set, requestAnimationFrame() {} });
const RulerAddon = window.RulerAddon.RulerAddon;

test("ruler tooltip uses the compact command-card typography", () => {
  assert.match(source, /font-family:'Cascadia Mono',Consolas,monospace/);
  assert.match(source, /font-size:12px/);
  assert.match(source, /padding:5px 10px/);
  assert.match(source, /border-radius:6px/);
});

function paintPresentation(isSplit, isGroupFocused, isPointerOver = false) {
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
  addon._cmdMarks = [
    { marker: { line: 10 }, exit: 0 },
    { marker: { line: 11 }, exit: 0 },
    { marker: { line: 20 }, exit: 2 },
  ];
  addon._bookmarks = [{ marker: { line: 30 } }];
  addon.setPresentation(isSplit, isGroupFocused);
  addon._isPointerOver = isPointerOver;
  addon._paint();
  return { operations, presentation: addon._strip.dataset.presentation };
}

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

test("focused split ruler uses the narrow Calm hierarchy and merges nearby marks", () => {
  const result = paintPresentation(true, true);
  const routine = operationsWithColor(result, "#9e9e9e");

  assert.equal(result.presentation, "calm-focused");
  assert.deepEqual(result.operations[0],
    { x: 4, y: 0, width: 10, height: 100, color: "#0c0c0c", alpha: 1 });
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

test("hover restores the full width, colors, and opacity", () => {
  const result = paintPresentation(true, false, true);

  assert.equal(result.presentation, "full");
  assert.equal(result.operations[0].x, 0);
  assert.equal(result.operations[0].width, 14);
  assert.equal(operationsWithColor(result, "#2ea043")[0].alpha, 1);
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
