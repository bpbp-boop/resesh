const assert = require("node:assert/strict");
const test = require("node:test");

const source = require("./support/mainWindowSource");

// Lock and unlock prompts wait on the user; the tab may close, move to another window or be
// locked by another prompt meanwhile.
test("lock and unlock prompts claim the tab and re-check it after the prompt", () => {
  const lock = source.match(/public async Task LockSessionAsync[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(lock, /tab\.IsLocked \|\| !ViewModel\.TryBeginLockPrompt\(tab\)/);
  assert.match(lock, /PromptPasswordAsync[\s\S]*?\|\| !CanStillClose\(tab\) \|\| tab\.IsLocked\)\s*return;\s*tab\.Lock\(password\)/);
  assert.match(lock, /catch \(Exception ex\)[\s\S]*?App\.ReportRecoverableError\(ex\)/);
  assert.match(lock, /finally[\s\S]*?ViewModel\.EndLockPrompt\(tab\)/);

  const unlock = source.match(/private async Task HandleUnlockAsync[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(unlock, /!tab\.IsLocked \|\| !ViewModel\.TryBeginLockPrompt\(tab\)/);
  assert.match(unlock, /finally[\s\S]*?ViewModel\.EndLockPrompt\(tab\)/);

  const claimed = source.match(/private async Task UnlockClaimedAsync[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(claimed, /PromptAsync\(\);[\s\S]*?!CanStillClose\(tab\) \|\| !tab\.IsLocked\)[\s\S]*?tab\.TryUnlock\(password\)/);
});
