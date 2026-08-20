const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const read = (...parts) => fs.readFileSync(path.join(__dirname, "..", ...parts), "utf8");
const dialog = read("src", "App", "Dialogs", "GlobalSettingsDialog.cs");
const highlightPanel = read("src", "App", "Dialogs", "HighlightEditorPanel.cs");
const agentPanel = read("src", "App", "Dialogs", "AgentAdapterPanel.cs");
const windowCode = read("src", "App", "MainWindow.xaml.cs");

test("settings is one tabbed dialog: General / Highlighting / Agents", () => {
  assert.match(dialog, /new SelectorBar\(\)/);
  assert.match(dialog, /SelectorBarItem \{ Text = "General" \}/);
  assert.match(dialog, /SelectorBarItem \{ Text = "Highlighting" \}/);
  assert.match(dialog, /SelectorBarItem \{ Text = "Agents" \}/);
  assert.match(dialog, /enum GlobalSettingsTab/);
  assert.match(dialog, /GlobalSettingsTab initialTab/);
});

test("the tab host keeps a fixed height so switching tabs doesn't resize the dialog", () => {
  assert.match(dialog, /TabContentHeight/);
  assert.match(dialog, /ContentDialogMaxHeight/);
  assert.match(dialog, /new ScrollViewer/);
});

test("the general tab keeps the grouped two-column layout and global-scope text", () => {
  assert.match(dialog, /SectionCard\("Appearance"/);
  assert.match(dialog, /SectionCard\("Terminal interaction"/);
  assert.match(dialog, /These settings apply throughout Sessions/);
});

test("the highlight editor lives inline in the Highlighting tab", () => {
  assert.match(dialog, /HighlightEditorPanel\.Create\(\(\) => applyPreview\(PreviewSettings\(\)\)\)/);
  assert.match(dialog, /apply immediately/);
  assert.match(highlightPanel, /class HighlightEditorPanel/);
  assert.match(highlightPanel, /Add custom rule/);
  assert.match(highlightPanel, /RefreshCombinedPreview/);
  assert.ok(
    !fs.existsSync(path.join(__dirname, "..", "src", "App", "Dialogs", "HighlightEditorDialog.cs")),
    "the standalone highlight editor dialog should be gone");
});

test("the rules list expands to fill the tab and the preview section pins to the bottom", () => {
  assert.match(dialog, /var highlightingTab = new Grid \{ Height = TabContentHeight/);
  assert.match(highlightPanel, /new RowDefinition \{ Height = new GridLength\(1, GridUnitType\.Star\) \}/);
  assert.match(highlightPanel, /Grid\.SetRow\(combinedPanel, 2\);/);
  assert.doesNotMatch(highlightPanel, /MaxHeight = 260/);
});

test("the standing preview sample is user-editable and shared with the rule form", () => {
  assert.match(highlightPanel, /var listSample = new TextBox/);
  assert.match(highlightPanel, /var sample = listSample\.Text;/);
  assert.match(highlightPanel, /listSample\.TextChanged \+= \(_, _\) => RefreshCombinedPreview\(\);/);
  assert.match(highlightPanel, /sampleBox\.Text = listSample\.Text;/);
  assert.match(highlightPanel, /listSample\.Text = sampleBox\.Text;/);
});

test("built-in rules are editable with a reset back to the shipped defaults", () => {
  assert.match(highlightPanel, /editButton\.IsEnabled = SelectedRule\(\) is not null;/);
  assert.match(highlightPanel, /deleteButton\.IsEnabled = SelectedRule\(\) is \{ IsBuiltin: false \};/);
  assert.match(highlightPanel, /SaveBuiltinOverride/);
  assert.match(highlightPanel, /Reset to default/);
  assert.match(highlightPanel, /ResetBuiltin/);
  assert.match(highlightPanel, /· edited/);

  const store = read("src", "Core", "Storage", "HighlightsStore.cs");
  assert.match(store, /public void SaveBuiltinOverride/);
  assert.match(store, /public bool ResetBuiltin/);
  assert.match(store, /public bool IsOverridden/);
  assert.match(store, /BuiltinOverrides/);
});

test("agent adapters live inline in the Agents tab as collapsible snippet rows", () => {
  assert.match(dialog, /AgentAdapterPanel\.Create\(\)/);
  assert.match(agentPanel, /class AgentAdapterPanel/);
  assert.match(agentPanel, /new Expander/);
  assert.match(agentPanel, /Content = "Copy"/);
  assert.match(agentPanel, /AgentAdapters\.SequenceReference/);
  assert.ok(
    !fs.existsSync(path.join(__dirname, "..", "src", "App", "Dialogs", "AgentAdapterDialog.cs")),
    "the standalone agent adapter dialog should be gone");
});

test("saving rebases only the dialog's fields onto the live settings", () => {
  const method = windowCode.match(/private async Task ShowSettingsAsync[\s\S]*?\n    \}/)?.[0] ?? "";
  assert.match(method, /App\.Settings\.Save\(App\.Settings\.Current with/);
  assert.match(method, /Theme = updated\.Theme/);
  assert.match(method, /AgentAlertSound = updated\.AgentAlertSound/);
  assert.doesNotMatch(windowCode, /GlobalSettingsAction/);
});

test("the tab strip's adapter snippets entry opens Settings on the Agents tab", () => {
  assert.match(windowCode, /ShowAgentAdaptersAsync\(\) => ShowSettingsAsync\(GlobalSettingsTab\.Agents\)/);
});
