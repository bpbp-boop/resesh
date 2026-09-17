const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { Terminal } = require("../src/Terminal/wwwroot/xterm.js");

const source = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "wwwroot", "addon-highlight.js"), "utf8");

function harness(t, options = {}) {
  const frames = new Map();
  let nextFrame = 0;
  const window = {};
  vm.runInNewContext(source, {
    window,
    requestAnimationFrame(fn) { frames.set(++nextFrame, fn); return nextFrame; },
    cancelAnimationFrame(id) { frames.delete(id); },
  });
  const term = new Terminal({ cols: 40, rows: 5, scrollback: 5, allowProposedApi: true, ...options });
  const addon = new window.HighlightAddon.HighlightAddon();
  const decorations = new Set();
  const register = term.registerDecoration.bind(term);
  term.registerDecoration = options => {
    const decoration = register(options);
    if (decoration) {
      decorations.add(decoration);
      decoration.onDispose(() => decorations.delete(decoration));
    }
    return decoration;
  };
  term.loadAddon(addon);
  addon.setRules([{ pattern: "error", color: "#ff5555" }]);
  t.after(() => term.dispose());
  const write = data => new Promise(resolve => {
    const listener = term.onWriteParsed(() => { listener.dispose(); resolve(); });
    term.write(data);
  });
  const flushFrames = () => {
    const pending = [...frames.values()];
    frames.clear();
    pending.forEach(fn => fn());
  };
  return { term, addon, decorations, write, flushFrames, frames };
}

function spans(decorations) {
  return [...decorations].map(d => [d.marker.line, d.options.x, d.options.width]);
}

test("parsed output is highlighted before a render frame is needed", async t => {
  const { write, decorations, frames } = harness(t);
  await write("an error");
  assert.deepEqual(spans(decorations), [[0, 3, 5]]);
  assert.equal(frames.size, 0);
});

test("identical log lines stay highlighted as full scrollback trims", async t => {
  const { term, write, decorations } = harness(t);
  await write("error\r\n".repeat(14));
  const old = [...decorations];
  assert.equal(old.length, 4);
  await write("error\r\n");
  assert.deepEqual(spans(decorations).map(s => s[0]).sort((a, b) => a - b), [5, 6, 7, 8]);
  assert.equal(old.filter(d => decorations.has(d)).length, 3,
    "surviving lines must retain their existing decorations, not repaint from scratch");
  assert.equal(term.buffer.active.length, 10);
});

test("cursor overwrites replace highlights without disposing the shared row marker", async t => {
  const { addon, write, decorations } = harness(t);
  addon.setRules([{ pattern: "error|warn", color: "#ff5555" }]);
  await write("error warn");
  assert.deepEqual(spans(decorations), [[0, 0, 5], [0, 6, 4]]);
  await write("\r\x1b[2Kwarn");
  assert.deepEqual(spans(decorations), [[0, 0, 4]]);
  await write("\r\x1b[2Knormal");
  assert.deepEqual(spans(decorations), []);
});

test("Unicode matches map UTF-16 indices to terminal cell columns", async t => {
  const { addon, write, decorations } = harness(t);
  addon.setRules([{ pattern: "界|😀|é|error", color: "#ff5555" }]);
  await write("界😀é error");
  // The bundled Unicode provider gives this emoji one cell, despite two UTF-16 units.
  assert.deepEqual(spans(decorations), [[0, 0, 2], [0, 2, 1], [0, 3, 1], [0, 5, 5]]);
});

test("viewport scrolling, alternate screen, resize and rule removal leave no stale highlights", async t => {
  const { term, addon, write, decorations, flushFrames } = harness(t);
  await write("error\r\n".repeat(8));
  term.scrollToTop();
  flushFrames();
  assert.deepEqual(spans(decorations).map(s => s[0]).sort((a, b) => a - b), [0, 1, 2, 3, 4]);
  await write("\x1b[?1049herror");
  assert.deepEqual(spans(decorations), []);
  await write("\x1b[?1049l");
  assert.equal(decorations.size, 5);
  term.resize(20, 4);
  flushFrames();
  assert.deepEqual(spans(decorations).map(s => s[0]).sort((a, b) => a - b),
    Array.from({ length: term.rows }, (_, i) => term.buffer.active.viewportY + i));
  addon.setRules([]);
  flushFrames();
  assert.deepEqual(spans(decorations), []);
  assert.equal(term.markers.length, 0);
});
