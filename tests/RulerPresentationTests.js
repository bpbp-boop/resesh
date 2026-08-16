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

test("ruler tooltip uses readable text", () => {
  assert.match(source, /font-size:14px/);
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
