const assert = require("node:assert/strict");
const test = require("node:test");

const source = require("./support/mainWindowSource");

// Every tree rebuild schedules up to ten expansion passes; only the latest rebuild's chain
// may keep running, or rapid rebuilds stack chains that push a superseded tree's state.
test("a newer tree rebuild supersedes the pending expansion sync", () => {
  assert.match(source, /private void ScheduleExpansionSync\(\) => ScheduleExpansionPass\(\+\+_expansionSyncGeneration, 10\);/);
  const pass = source.match(/private void ScheduleExpansionPass[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(pass, /TryPost\([\s\S]*?\{\s*if \(generation != _expansionSyncGeneration \|\| _windowClosed\)\s*return;/);
  assert.match(pass, /timer\.OnTick\(\(\) => ScheduleExpansionPass\(generation, remainingPasses - 1\)\);/);
  assert.doesNotMatch(source, /ScheduleExpansionSync\(remainingPasses/);
});
