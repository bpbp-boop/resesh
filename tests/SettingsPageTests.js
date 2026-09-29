const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const read = (...parts) => fs.readFileSync(path.join(__dirname, "..", ...parts), "utf8");
const page = read("src", "App", "Controls", "SettingsPage.xaml");
const pageCode = read("src", "App", "Controls", "SettingsPage.xaml.cs");
const settings = read("src", "App", "ViewModels", "SettingsViewModel.cs");
const highlightPanel = read("src", "App", "Controls", "HighlightRulesEditor.xaml") + read("src", "App", "Controls", "HighlightRulesEditor.xaml.cs");
const agentPanel = read("src", "App", "Controls", "AgentAdaptersView.xaml") + read("src", "App", "Controls", "AgentAdaptersView.xaml.cs");
const windowCode = read("src", "App", "MainWindow.xaml.cs");
const windowXaml = read("src", "App", "MainWindow.xaml");

test("settings is an app page tab, not a dialog", () => {
  assert.ok(
    !fs.existsSync(path.join(__dirname, "..", "src", "App", "Dialogs", "GlobalSettingsDialog.cs")),
    "the Settings dialog should be gone");
  assert.doesNotMatch(page + pageCode, /ContentDialog/);
  const show = windowCode.match(/private Task ShowSettingsAsync\(GlobalSettingsTarget target\)[\s\S]*?\n    }/)?.[0] ?? "";
  // One Settings tab per window: opening it again reuses the open one.
  assert.match(show, /OpenAppPage\(AppPage\.Settings/);
  assert.match(show, /page\.Navigate\(target\)/);
  assert.match(windowCode, /ViewModel\.FindAppPage\(page\) is \{ \} existing/);
});

test("settings has General / Recording / Highlighting / Agents / Keyboard Shortcuts sections", () => {
  for (const section of ["General", "Recording", "Highlighting", "Agents", "Shortcuts"])
    assert.match(page, new RegExp(`Tag="${section}"`), section);
  // The targets live with the view models, whose commands open Settings at a field.
  assert.match(read("src", "App", "ViewModels", "SettingsTargets.cs"), /enum GlobalSettingsTarget/);
  assert.match(pageCode, /public void Navigate\(GlobalSettingsTarget target\)/);
  assert.match(pageCode, /StartBringIntoView/);
  assert.match(page, /<controls:ShortcutsReference/);
});

test("changes apply as they are made, with no Save or Cancel", () => {
  assert.doesNotMatch(page, /PrimaryButtonText|"Save"|"Cancel"/);
  assert.match(settings, /_environment\.Save\(updated\)/);
  assert.match(settings, /SettingChanged\?\.Invoke\(property\)/);
  assert.match(windowCode, /settings\.SettingChanged \+= property => App\.ApplySettingChange\(property, source: settings\)/);
});

test("the page follows the live application palette", () => {
  assert.match(page, /Background="\{StaticResource SessionShellBrush\}"/);
  assert.match(page, /Foreground="\{StaticResource SessionTreeForegroundBrush\}"/);
  assert.match(page, /x:Key="SettingsCardBackground" ResourceKey="SettingsCardBackgroundBrush"/);
});

test("settings fields and sections have stable automation IDs", () => {
  for (const automationId of [
    "SettingsTheme",
    "SettingsFontFamily",
    "SettingsFontSize",
    "SettingsScrollback",
    "SettingsCopyOnSelect",
    "SettingsRightClickPaste",
    "SettingsShowStatusBar",
    "SettingsReopenLastLayout",
    "SettingsLaunchAtSignIn",
    "SettingsConfirmCloseActiveSessions",
    "SettingsWriteCrashReports",
    "SettingsKeepCommandHistory",
    "SettingsCommandHistoryDays",
    "SettingsClearCommandHistory",
    "SettingsRecordingDirectory",
    "SettingsAlwaysRecord",
    "SettingsRewindMinutes",
    "SettingsRewindMegabytes",
    "SettingsShowAgentIcons",
    "SettingsAgentAlertFlash",
    "SettingsAgentAlertSound",
    "SettingsSectionSelector",
    "SettingsGeneralTab",
    "SettingsRecordingTab",
    "SettingsHighlightingTab",
    "SettingsAgentsTab",
    "SettingsShortcutsTab",
    "SettingsTabContent",
  ]) {
    assert.ok(page.includes(`AutomationProperties.AutomationId="${automationId}"`), `missing ${automationId}`);
  }
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
  assert.match(highlightPanel, /\$"SettingsHighlightRuleEnabled_\{Rule\.Id\}"/);

  assert.match(agentPanel, /\$"SettingsAgentAdapter_\{index\}"/);
  assert.match(agentPanel, /\$"SettingsAgentAdapterCopy_\{index\}"/);
  assert.match(agentPanel, /"SettingsAgentProtocolReference"/);
});

test("highlighting rules commit after each change and reach every window", () => {
  assert.match(pageCode, /new HighlightRulesEditor\(_highlightDraft\)[\s\S]*?highlightEditor\.Changed \+= CommitHighlights/);
  assert.match(pageCode, /App\.Highlights\.CommitDraft\(_highlightDraft\)[\s\S]*?App\.RefreshHighlightsInAllWindows\(\)/);
  assert.match(highlightPanel, /class HighlightRulesEditor/);
  assert.match(highlightPanel, /Add custom rule/);
  assert.match(highlightPanel, /RefreshCombinedPreview/);
  for (const gone of ["HighlightEditorDialog.cs", "HighlightEditorPanel.cs", "AgentAdapterPanel.cs", "SettingsLayout.cs"])
    assert.ok(!fs.existsSync(path.join(__dirname, "..", "src", "App", "Dialogs", gone)), `${gone} should be gone`);
});

test("the standing preview sample is user-editable and shared with the rule form", () => {
  assert.match(highlightPanel, /x:Name="ListSampleBox"/);
  assert.match(highlightPanel, /var sample = ListSampleBox\.Text;/);
  assert.match(highlightPanel, /ListSample_TextChanged\(object sender, TextChangedEventArgs e\) => RefreshCombinedPreview\(\);/);
  assert.match(highlightPanel, /FormSampleBox\.Text = ListSampleBox\.Text;/);
  assert.match(highlightPanel, /ListSampleBox\.Text = FormSampleBox\.Text;/);
});

test("built-in rules are editable with a reset back to the shipped defaults", () => {
  assert.match(highlightPanel, /EditButton\.IsEnabled = SelectedRule is not null;/);
  assert.match(highlightPanel, /DeleteButton\.IsEnabled = SelectedRule is \{ IsBuiltin: false \};/);
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

test("the Agents section groups controls and keeps adapter details collapsed", () => {
  assert.match(page, /Text="Tab display"/);
  assert.match(page, /Text="Background alerts"/);
  assert.match(page, /Text="Agent adapters"/);
  assert.match(page, /<controls:AgentAdaptersView \/>/);
  assert.match(agentPanel, /Manual setup:/);
  assert.match(agentPanel, /IsExpanded="False"/);
  assert.match(agentPanel, /Content="Copy"/);
  assert.match(agentPanel, /Header="Protocol reference"/);
  assert.ok(
    !fs.existsSync(path.join(__dirname, "..", "src", "App", "Dialogs", "AgentAdapterDialog.cs")),
    "the standalone agent adapter dialog should be gone");
});

test("background alert choices are unavailable when agent icons are off", () => {
  assert.match(settings, /public bool AgentAlertsEnabled => ShowAgentIcons;/);
  assert.match(page, /x:Name="AgentFlashCard"[\s\S]*?IsEnabled="\{x:Bind ViewModel\.AgentAlertsEnabled, Mode=OneWay\}"/);
  assert.match(page, /x:Name="AgentSoundCard"[\s\S]*?IsEnabled="\{x:Bind ViewModel\.AgentAlertsEnabled, Mode=OneWay\}"/);
});

test("the status bar is visible by default and follows the saved setting", () => {
  assert.match(page, /IsOn="\{x:Bind ViewModel\.ShowStatusBar, Mode=TwoWay\}"/);
  assert.match(windowXaml, /x:Name="StatusBar"[\s\S]*?AutomationProperties\.AutomationId="StatusBar"/);
  assert.match(windowXaml, /x:Name="StatusBar"[\s\S]*?Grid\.Row="2"/);
  assert.match(windowCode, /ApplyStatusBarVisibility\(settings\.ShowStatusBar\)/);
  assert.match(windowCode, /StatusBarMenuItem\.IsChecked = visible/);
});

test("each change rebases onto the live settings", () => {
  assert.match(settings, /var current = Current;\s*var updated = change\(current\);/);
  assert.doesNotMatch(windowCode, /GlobalSettingsAction/);
});

test("the tab strip's adapter snippets entry opens Settings on the Agents section", () => {
  assert.match(windowCode, /ShowAgentAdaptersAsync\(\) => ShowSettingsAsync\(GlobalSettingsTarget\.Agents\)/);
});
