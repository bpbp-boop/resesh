const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const page = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "wwwroot", "terminal.html"), "utf8");
const start = page.indexOf("[7377, 9, 777].forEach");
const end = page.indexOf("if (term.onBell)", start);

function route(code, payload) {
  const handlers = new Map();
  const messages = [];
  vm.runInNewContext(page.slice(start, end), {
    term: { parser: { registerOscHandler(code, handler) { handlers.set(code, handler); } } },
    postAgent(message) { messages.push(message); },
  });
  assert.equal(handlers.get(code)(payload), true);
  return messages;
}

test("OSC 9 directory reports use their own channel without truncating paths", () => {
  for (const data of ['9;"C:\\My Files"', '9;', '9']) {
    const messages = route(9, data);
    assert.equal(messages.length, 1);
    assert.equal(messages[0].type, "windowsWorkingDirectory");
    assert.equal(messages[0].data, data);
  }
  assert.equal(route(9, "9;C:\\" + "a".repeat(4096)).length, 0);
});

test("ordinary notifications and structured agent events keep their channel", () => {
  for (const [code, data] of [[9, "Build done"], [777, "notify;Done;Build"], [7377, "agent;state=done"]]) {
    const messages = route(code, data);
    assert.equal(messages.length, 1);
    assert.equal(messages[0].type, "agentOsc");
    assert.equal(messages[0].code, code);
    assert.equal(messages[0].data, data);
  }
  const [bounded] = route(9, "x".repeat(3000));
  assert.equal(bounded.data.length, 2048);
});
