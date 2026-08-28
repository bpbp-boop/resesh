const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const read = (...parts) => fs.readFileSync(path.join(__dirname, "..", ...parts), "utf8");
const dialog = read("src", "App", "Dialogs", "GlobalSettingsDialog.cs");
const highlightPanel = read("src", "App", "Dialogs", "HighlightEditorPanel.cs");
const agentPanel = read("src", "App", "Dialogs", "AgentAdapterPanel.cs");
const windowCode = read("src", "App", "MainWindow.xaml.cs");

test("settings is one targeted dialog with General / Recording / Highlighting / Agents tabs", () => {
  assert.match(dialog, /new SelectorBar\(\)/);
  assert.match(dialog, /SelectorBarItem \{ Text = "General" \}/);
  assert.match(dialog, /SelectorBarItem \{ Text = "Recording" \}/);
  assert.match(dialog, /SelectorBarItem \{ Text = "Highlighting" \}/);
  assert.match(dialog, /SelectorBarItem \{ Text = "Agents" \}/);
  assert.match(dialog, /enum GlobalSettingsTarget/);
  assert.match(dialog, /GlobalSettingsTarget initialTarget/);
});

test("the tab host keeps one responsive height so switching tabs doesn't resize the dialog", () => {
  assert.match(dialog, /PreferredTabContentHeight/);
  assert.match(dialog, /GetDialogContentSize\(xamlRoot\)/);
  assert.match(dialog, /host\.Height = tabContentHeight/);
  assert.match(dialog, /ContentDialogMaxHeight/);
  assert.match(dialog, /new ScrollViewer/);
});

test("the general tab groups related settings and stacks cards on narrow windows", () => {
  assert.match(dialog, /SectionCard\("Appearance"/);
  assert.match(dialog, /SectionCard\("Terminal interaction"/);
  assert.match(dialog, /ConfigureResponsiveColumns\(generalColumns, stackCards/);
  assert.match(dialog, /StackedCardThreshold/);
  assert.match(dialog, /These settings apply throughout resesh/);
});

test("settings tracks window size and reflows fields without horizontal scrolling", () => {
  assert.match(dialog, /xamlRoot\.Changed \+= XamlRootChanged/);
  assert.match(dialog, /xamlRoot\.Changed -= XamlRootChanged/);
  assert.match(dialog, /ConfigureResponsiveColumns\(numberGrid, stackFields/);
  assert.match(dialog, /ConfigureResponsiveColumns\(rewindGrid, stackFields/);
  assert.match(dialog, /HorizontalScrollBarVisibility = ScrollBarVisibility\.Disabled/);
});

test("the complete dialog follows the live application palette", () => {
  assert.match(dialog, /Background = \(Brush\)Application\.Current\.Resources\["SessionShellBrush"\]/);
  assert.match(dialog, /BorderBrush = \(Brush\)Application\.Current\.Resources\["SettingsCardBorderBrush"\]/);
  assert.match(dialog, /Foreground = \(Brush\)Application\.Current\.Resources\["SessionTreeForegroundBrush"\]/);
  assert.match(dialog, /Background = \(Brush\)Application\.Current\.Resources\["SettingsCardBackgroundBrush"\]/);
});

test("settings fields and section tabs have stable automation IDs", () => {
  for (const [control, automationId] of [
    ["theme", "SettingsTheme"],
    ["fontFamily", "SettingsFontFamily"],
    ["fontSize", "SettingsFontSize"],
    ["scrollback", "SettingsScrollback"],
    ["copyOnSelect", "SettingsCopyOnSelect"],
    ["rightClickPaste", "SettingsRightClickPaste"],
    ["reopenLastLayout", "SettingsReopenLastLayout"],
    ["recordingDirectory", "SettingsRecordingDirectory"],
    ["alwaysRecord", "SettingsAlwaysRecord"],
    ["rewindMinutes", "SettingsRewindMinutes"],
    ["rewindMegabytes", "SettingsRewindMegabytes"],
    ["agentIcons", "SettingsShowAgentIcons"],
    ["agentFlash", "SettingsAgentAlertFlash"],
    ["agentSound", "SettingsAgentAlertSound"],
  ]) {
    assert.match(dialog, new RegExp(`SetAutomationId\\(${control}, "${automationId}"\\)`));
  }

  assert.match(dialog, /SetAutomationId\(bar, "SettingsSectionSelector"\)/);
  assert.match(dialog, /SetAutomationId\(barItems\[0\], "SettingsGeneralTab"\)/);
  assert.match(dialog, /SetAutomationId\(barItems\[1\], "SettingsRecordingTab"\)/);
  assert.match(dialog, /SetAutomationId\(barItems\[2\], "SettingsHighlightingTab"\)/);
  assert.match(dialog, /SetAutomationId\(barItems\[3\], "SettingsAgentsTab"\)/);
  assert.match(dialog, /SetAutomationId\(dialog, "GlobalSettingsDialog"\)/);
});

test("inline Settings editors expose stable automation IDs", () => {
  for (const automationId of [
    "SettingsHighlightRules",
    "SettingsHighlightAdd",
    "SettingsHighlightEdit",
    "SettingsHighlightDelete",
    "SettingsHighlightListSample",
    "SettingsHighlightName",
    "SettingsHighlightPattern",
    "SettingsHighlightColor",
    "SettingsHighlightBold",
    "SettingsHighlightUnderline",
    "SettingsHighlightMatchCase",
    "SettingsHighlightOverview",
    "SettingsHighlightFormSample",
    "SettingsHighlightSave",
    "SettingsHighlightCancel",
    "SettingsHighlightReset",
  ]) {
    assert.ok(highlightPanel.includes(`"${automationId}"`), `missing ${automationId}`);
  }
  assert.match(highlightPanel, /\$"SettingsHighlightRuleEnabled_\{rule\.Id\}"/);

  assert.match(agentPanel, /\$"SettingsAgentAdapter_\{index\}"/);
  assert.match(agentPanel, /\$"SettingsAgentAdapterCopy_\{index\}"/);
  assert.match(agentPanel, /"SettingsAgentProtocolReference"/);
});

test("the highlight editor lives inline in the Highlighting tab", () => {
  assert.match(dialog, /HighlightEditorPanel\.Create\(applyHighlightChanges\)/);
  assert.match(dialog, /apply immediately/);
  assert.match(highlightPanel, /class HighlightEditorPanel/);
  assert.match(highlightPanel, /Add custom rule/);
  assert.match(highlightPanel, /RefreshCombinedPreview/);
  assert.ok(
    !fs.existsSync(path.join(__dirname, "..", "src", "App", "Dialogs", "HighlightEditorDialog.cs")),
    "the standalone highlight editor dialog should be gone");
});

test("the rules list expands to fill the tab and the preview section pins to the bottom", () => {
  assert.match(dialog, /var highlightingTab = new Grid \{ Height = PreferredTabContentHeight/);
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

test("the Agents tab groups controls and keeps adapter details collapsed", () => {
  assert.match(dialog, /SectionCard\(\s*"Tab display"/);
  assert.match(dialog, /SectionCard\(\s*"Background alerts"/);
  assert.match(dialog, /SectionCard\(\s*"Agent adapters"/);
  assert.match(dialog, /AgentAdapterPanel\.Create\(\)/);
  assert.match(agentPanel, /new InfoBar/);
  assert.match(agentPanel, /IsExpanded = false/);
  assert.match(agentPanel, /Content = "Copy"/);
  assert.match(agentPanel, /ProtocolReference\(\)/);
  assert.ok(
    !fs.existsSync(path.join(__dirname, "..", "src", "App", "Dialogs", "AgentAdapterDialog.cs")),
    "the standalone agent adapter dialog should be gone");
});

test("background alert choices are unavailable when agent icons are off", () => {
  assert.match(dialog, /agentFlash\.IsEnabled = agentIcons\.IsOn/);
  assert.match(dialog, /agentSound\.IsEnabled = agentIcons\.IsOn/);
  assert.match(dialog, /agentIcons\.Toggled \+= \(_, _\) => SyncAgentAlertControls\(\)/);
});

test("saving rebases only the dialog's fields onto the live settings", () => {
  const method = windowCode.match(/private async Task ShowSettingsAsync[\s\S]*?\n    \}/)?.[0] ?? "";
  assert.match(method, /App\.Settings\.Save\(App\.Settings\.Current with/);
  assert.match(method, /Theme = updated\.Theme/);
  assert.match(method, /AgentAlertSound = updated\.AgentAlertSound/);
  assert.doesNotMatch(windowCode, /GlobalSettingsAction/);
});

test("the tab strip's adapter snippets entry opens Settings on the Agents tab", () => {
  assert.match(windowCode, /ShowAgentAdaptersAsync\(\) => ShowSettingsAsync\(GlobalSettingsTarget\.Agents\)/);
});
