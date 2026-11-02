const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const source = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "MainWindow.Import.cs"), "utf8");
const method = (name) =>
  source.match(new RegExp(String.raw`private (?:async )?Task ${name}\([\s\S]*?\n    }\r?\n`))?.[0] ?? "";

// Backup export, backup import and session import are chains of dialogs and pickers that
// write the app's stores. One runs at a time, and none touches a window that closed.
test("backup and import wizards run one at a time through a guarded entry", () => {
  assert.match(source, /private static MainWindow\? s_wizardWindow;/);
  assert.match(source, /private Task ExportBackupAsync\(\) => RunWizardAsync\("Export failed", ExportBackupWizardAsync\);/);
  assert.match(source, /private Task ImportBackupAsync\(\) => RunWizardAsync\("Import failed", ImportBackupWizardAsync\);/);
  assert.match(source, /private Task ImportSessionsAsync\(\) => RunWizardAsync\("Import failed", ImportSessionsWizardAsync\);/);

  const run = method("RunWizardAsync");
  assert.match(run, /if \(_windowClosed\)\s*return;/);
  assert.match(run, /s_wizardWindow is \{ _windowClosed: false \} running\)\s*\{\s*running\.BringToFront\(\);\s*return;/);
  assert.match(run, /catch \(Exception ex\)\s*\{\s*await ShowWizardErrorAsync\(failureTitle, ex\);/);
  assert.match(run, /finally\s*\{\s*if \(ReferenceEquals\(s_wizardWindow, this\)\)\s*s_wizardWindow = null;/);
});

test("a wizard's failure never throws from its error path", () => {
  const error = method("ShowWizardErrorAsync");
  assert.match(error, /if \(_windowClosed \|\| Root\.XamlRoot is null\)\s*\{\s*App\.ReportRecoverableError\(exception\);\s*return;/);
  assert.match(error, /catch \(Exception displayError\)\s*\{\s*App\.ReportRecoverableError\(displayError\);/);
  // The wizards themselves leave failures to RunWizardAsync.
  for (const name of ["ExportBackupWizardAsync", "ImportBackupWizardAsync", "ImportSessionsWizardAsync"]) {
    const body = method(name);
    assert.ok(body.length > 0, name);
    assert.doesNotMatch(body, /catch \(/, name);
  }
});

test("wizards stop showing dialogs once their window has closed", () => {
  for (const name of ["ExportBackupWizardAsync", "ImportBackupWizardAsync", "ImportSessionsWizardAsync"]) {
    const body = method(name);
    // Every await of a picker or background task is followed by a closed-window check
    // before the next dialog.
    const awaits = [...body.matchAll(/await (?:picker\.Pick\w+|Task\.Run|Task\.WhenAll|GetSelectedImportScanAsync)[^;]*;/g)];
    for (const { 0: awaited, index } of awaits) {
      // Measured from the await itself: an awaited condition holds its check in its block.
      const after = body.slice(index + 5);
      const nextDialog = after.search(/ShowModalAsync|ShowMessageAsync|PromptPasswordAsync|ReviewImportAsync/);
      const nextCheck = after.search(/_windowClosed/);
      if (nextDialog >= 0)
        assert.ok(nextCheck >= 0 && nextCheck < nextDialog, `${name}: no check after ${awaited.slice(0, 40)}`);
    }
  }
});
