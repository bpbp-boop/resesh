const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const read = (...parts) => fs.readFileSync(path.join(__dirname, "..", ...parts), "utf8");
const sessionDialog = read("src", "App", "Dialogs", "SessionEditDialog.xaml.cs");
const localDialog = read("src", "App", "Dialogs", "LocalProfileEditDialog.xaml.cs");
const appearance = read("src", "App", "Controls", "SessionAppearanceForm.xaml.cs");
const windowCode = require("./support/mainWindowSource");

test("the session editors edit the saved session instead of building a new one", () => {
  // A new Session { ... } drops every field the dialog doesn't list, including ones added later.
  for (const [name, code] of [["SessionEditDialog", sessionDialog], ["LocalProfileEditDialog", localDialog], ["SessionAppearanceForm", appearance]]) {
    assert.doesNotMatch(code.replace(/new Session \{ Id = Guid\.NewGuid\(\) \}/g, ""), /new Session\b/, name);
    assert.doesNotMatch(code, /new TerminalOverrides\s*\{/, name);
  }
  assert.match(sessionDialog, /AppearanceForm\.ApplyTo\(basis with/);
  assert.match(localDialog, /AppearanceForm\.ApplyTo\(basis with/);
  assert.match(localDialog, /Local = \(basis\.Local \?\? new LocalTarget\(\)\) with/);
  assert.match(appearance, /var overrides = saved with/);
});

test("an editor's save is merged into the session as saved when it closes", () => {
  // Every editor save goes through RebaseOnSaved, so changes made elsewhere meanwhile survive.
  assert.doesNotMatch(windowCode, /UpdateSession\(result,/);
  assert.equal((windowCode.match(/else if \(RebaseOnSaved\(existing, result\) is \{ \} merged\)/g) ?? []).length, 2);
  assert.match(windowCode, /SessionMerge\.Rebase\(original, edited, latest\)/);
  // The tab's session options open the same editor, not a second save path.
  assert.doesNotMatch(windowCode, /new SessionEditDialog\([\s\S]*?new SessionEditDialog\(/);
});

test("number limits come from SettingRanges, not XAML literals", () => {
  const settingsXaml = read("src", "App", "Controls", "SettingsPage.xaml");
  const formXaml = read("src", "App", "Controls", "SessionAppearanceForm.xaml");
  for (const name of ["FontSizeBox", "ScrollbackBox", "HistoryDaysBox"]) {
    const box = settingsXaml.match(new RegExp(`x:Name="${name}"[^>]*>`))?.[0] ?? "";
    assert.ok(box.length > 0, name);
    assert.doesNotMatch(box, /Minimum=|Maximum=/, name);
  }
  assert.doesNotMatch(formXaml, /Minimum=|Maximum=/);
  assert.match(appearance, /NumberBoxRanges\.Limit\(OverrideFontSizeBox, SettingRanges\.FontSize\)/);
  assert.match(appearance, /NumberBoxRanges\.Limit\(OverrideScrollbackBox, SettingRanges\.Scrollback\)/);
  const settingsCode = read("src", "App", "Controls", "SettingsPage.xaml.cs");
  assert.match(settingsCode, /NumberBoxRanges\.Limit\(FontSizeBox, SettingRanges\.FontSize\)/);
  assert.match(settingsCode, /NumberBoxRanges\.Limit\(ScrollbackBox, SettingRanges\.Scrollback\)/);
  assert.match(settingsCode, /NumberBoxRanges\.Limit\(HistoryDaysBox, SettingRanges\.CommandHistoryDays\)/);
  const viewModel = read("src", "App", "ViewModels", "SettingsViewModel.cs");
  assert.doesNotMatch(viewModel, /Whole\(value, (8, 32|1000, 100000|1, 3650)\)/);
});

test("colour tags come from one list in Core", () => {
  for (const file of [sessionDialog, localDialog, read("src", "App", "Dialogs", "BulkEditSessionsDialog.xaml.cs"), appearance])
    assert.doesNotMatch(file, /ColorChoices|"#[0-9A-Fa-f]{6}"/);
  assert.match(appearance, /SessionColors\.ChoicesFor\(session\?\.ColorTag\)/);
});

test("PowerShell integration and persistence follow one rule", () => {
  assert.match(sessionDialog, /SessionRules\.CanBePersistent\(shellIntegration\)/);
  assert.match(sessionDialog, /SessionRules\.PersistentPowerShellConflict/);
  assert.doesNotMatch(sessionDialog, /Turn off Persistent session/);
  assert.match(read("src", "App", "Dialogs", "BulkEditSessionsDialog.xaml.cs"), /edit\.PersistentSkipped\(_sessions\)/);
});

test("session theme picks preserve untouched ids and expose the real focus target", () => {
  const form = read("src", "App", "Controls", "SessionAppearanceForm.xaml");
  assert.match(form, /<controls:ThemePickerButton x:Name="OverrideThemePicker" InheritLabel=/);
  assert.match(appearance, /OverrideThemePicker\.ThemeId = overrides\.Theme/);
  assert.match(appearance, /Theme = Keep\(s => s\.Theme, saved\.Theme, \(\) => now\.Theme\)/);
  assert.match(appearance, /SessionSettingsTarget\.Theme => OverrideThemePicker\.FocusTarget/);
});

test("the shared theme picker searches both galleries and only emits user choices", () => {
  const picker = read("src", "App", "Controls", "ThemePickerButton.xaml.cs");
  const showChoice = picker.match(/private void ShowChoice\(\)[\s\S]*?(?=\n    private void)/)?.[0];
  assert.ok(showChoice);
  assert.match(showChoice, /theme\?\.Name \?\? ThemeId \?\? InheritLabel/);
  assert.doesNotMatch(showChoice, /SelectionChanged\?\.Invoke/);
  for (const mode of ["Light", "Dark"]) {
    assert.match(picker, new RegExp(`${mode}Themes\\.Query = Filter\\.Query`));
    assert.match(picker, new RegExp(`${mode}Themes\\.SelectedTheme =`));
  }
  assert.match(picker, /Gallery_SelectionChanged[^;]*=> Pick\(theme\.Id\)/);
  assert.match(picker, /InheritButton_Click[^;]*=> Pick\(null\)/);
  assert.match(picker, /ThemeId = id;[\s\S]*?PickerFlyout\.Hide\(\);[\s\S]*?if \(changed\)[\s\S]*?SelectionChanged\?\.Invoke/);
  assert.match(picker, /AutomationProperties\.SetName\(PickerButton,/);
});

test("bulk theme edits keep no-change, inherit and a chosen theme distinct", () => {
  const bulk = read("src", "App", "Dialogs", "BulkEditSessionsDialog.xaml.cs");
  const xaml = read("src", "App", "Dialogs", "BulkEditSessionsDialog.xaml");
  assert.match(xaml, /<controls:ThemePickerButton[\s\S]*?x:Name="ThemeChoicePicker"/);
  assert.match(bulk, /ThemeChoicePicker\.IsEnabled = ThemeBox\.SelectedIndex == 2/);
  assert.match(bulk, /SetTheme = ThemeBox\.SelectedIndex > 0/);
  assert.match(bulk, /Theme = ThemeBox\.SelectedIndex == 2 \? ThemeChoicePicker\.ThemeId : null/);
});
