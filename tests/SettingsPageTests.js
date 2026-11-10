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
const windowCode = require("./support/mainWindowSource");
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

test("settings exposes its first-class sections", () => {
  for (const section of ["General", "Theme", "Recording", "Highlighting", "Agents", "Shortcuts"])
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
  // Every window follows the store, so a change saved by any page reaches them all. What a
  // change redoes is SettingsEffects' (tested in AppLogic.Tests); the page skips its own echo.
  assert.match(windowCode, /App\.Settings\.Changed \+= Settings_Changed;/);
  assert.match(windowCode, /ApplySettings\(SettingsEffects\.Between\(before, after\)\)/);
  assert.match(settings, /ReferenceEquals\(Current, _ownSave\)/);
});

test("the page follows the live application palette", () => {
  assert.match(page, /Foreground="\{StaticResource SessionTreeForegroundBrush\}"/);
  assert.match(page, /x:Key="SettingsCardBackground" ResourceKey="SettingsCardBackgroundBrush"/);
});

test("settings fields and sections have stable automation IDs", () => {
  for (const automationId of [
    "SettingsAppearance",
    "SettingsLightTheme",
    "SettingsDarkTheme",
    "SettingsTheme",
    "SettingsFontFamily",
    "SettingsFontSize",
    "SettingsScrollback",
    "SettingsCopyOnSelect",
    "SettingsRightClickPaste",
    "SettingsMouseCapture",
    "SettingsShowStatusBar",
    "SettingsSessionsPaneLayout",
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
    "SettingsThemeTab",
    "SettingsRecordingTab",
    "SettingsHighlightingTab",
    "SettingsAgentsTab",
    "SettingsShortcutsTab",
    "SettingsTabContent",
  ]) {
    assert.ok(page.includes(`AutomationProperties.AutomationId="${automationId}"`), `missing ${automationId}`);
  }
});

test("theme galleries are virtualized, searchable and keep their own selection", () => {
  const gallery = read("src", "App", "Controls", "ThemeGallery.xaml");
  const galleryCode = read("src", "App", "Controls", "ThemeGallery.xaml.cs");
  assert.match(gallery, /<ItemsStackPanel Orientation="Horizontal"/);
  assert.match(gallery, /SelectionMode="Single"/);
  assert.match(galleryCode, /StringComparison\.OrdinalIgnoreCase/);
  for (const gallery of ["Fixed", "Light", "Dark"])
    assert.match(pageCode, new RegExp(`${gallery}Gallery\\.Query = ThemeFilter\\.Query`));
  // Light and dark are a filter only where one gallery mixes both (Theme).
  assert.match(pageCode, /ThemeFilter\.ShowModes = !ViewModel\.IsFollowingWindows/);
  assert.match(pageCode, /ViewModel\.ThemeModeChanged\(\)/);
  assert.match(galleryCode, /if \(_updating \|\| ThemeList\.SelectedItem is not ThemeChoice picked\)/);
});

test("Settings lays themes out in wrapping grids that build only the cards in view", () => {
  const rows = read("src", "App", "Controls", "ThemeCategoryRows.cs");
  const grid = read("src", "App", "Controls", "ThemeGrid.xaml");
  const gridCode = read("src", "App", "Controls", "ThemeGrid.xaml.cs");
  assert.match(rows, /new ThemeGrid \{/);
  assert.match(grid, /<ItemsWrapGrid Orientation="Horizontal"/);
  // No ScrollViewer of its own: a wheel turn over it scrolls the page, and the ItemsWrapGrid
  // virtualizes against the page's viewport instead of building every card.
  assert.match(grid, /<ControlTemplate TargetType="ListView">\s*<ItemsPresenter \/>\s*<\/ControlTemplate>/);
  // A grid out of view gets its themes once it is laid out in view, its height held till then.
  assert.match(gridCode, /EffectiveViewportChanged \+= \(_, _\) => PopulateIfVisible\(\)/);
  assert.match(gridCode, /MinHeight = RowCount \* RowHeight/);
  // Resetting the list scrolls the page to the top, so its items are edited in place.
  assert.equal((gridCode.match(/ItemsSource =/g) ?? []).length, 1);
  assert.doesNotMatch(gridCode, /_items\.Clear\(\)/);
  // The current theme shows even when it is past the cut.
  assert.match(gridCode, /shown\[\^1\] = match/);
});

test("theme sections link to their category tab and back, and shuffle in place", () => {
  const rows = read("src", "App", "Controls", "ThemeCategoryRows.cs");
  const gridCode = read("src", "App", "Controls", "ThemeGrid.xaml.cs");
  const filter = read("src", "App", "Controls", "ThemeFilterBar.xaml.cs");
  assert.match(rows, /"See all"/);
  assert.match(rows, /"Show less"/);
  assert.match(rows, /grid\.Shuffle\(\)/);
  assert.match(gridCode, /Random\.Shared\.Shuffle/);
  assert.match(rows, /grid\.SortByName\(\)/);
  // "See all" shows every theme of the category, not a few more rows.
  assert.match(rows, /section\.Grid\.Rows = focused \? null : OverviewRows/);
  // Every section, Other included, has a tab to open.
  assert.match(filter, /ThemeCategories\.OtherName/);
  for (const gallery of ["Fixed", "Light", "Dark"])
    assert.match(pageCode, new RegExp(`${gallery}Gallery\\.CategoryRequested \\+= ThemeFilter\\.ShowCategory`));
});

test("the Settings tab names the open section", () => {
  assert.match(pageCode, /SectionShown\?\.Invoke\(section\)/);
  assert.match(read("src", "App", "MainWindow.xaml.cs"), /page\.SectionShown \+= section => tab\.SettingsSection = section/);
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
  assert.match(pageCode, /App\.Highlights\.CommitDraft\(_highlightDraft\)/);
  assert.match(windowCode, /App\.Highlights\.Changed \+= Highlights_Changed;/);
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

  const store = read("src", "Core", "Storage", "HighlightsStore.cs");
  assert.match(store, /public void SaveBuiltinOverride/);
  assert.match(store, /public bool ResetBuiltin/);
  assert.match(store, /public bool IsOverridden/);
  assert.match(store, /BuiltinOverrides/);
});

test("the Agents section nests alerts under agent icons and keeps adapter details collapsed", () => {
  // Alerts need the recognition the icons turn on; nesting shows that without helper text.
  assert.match(page, /x:Name="AgentIconsCard"[\s\S]*?<toolkit:SettingsExpander\.Items>[\s\S]*?x:Name="AgentFlashCard"[\s\S]*?x:Name="AgentSoundCard"[\s\S]*?<\/toolkit:SettingsExpander\.Items>/);
  assert.match(pageCode, /AgentAlertFlash or GlobalSettingsTarget\.AgentAlertSound\)\s*AgentIconsCard\.IsExpanded = true;/);
  assert.match(page, /<controls:AgentAdaptersView \/>/);
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

test("the SSH algorithm picker follows settings changed in another window", () => {
  // The picker is built in code, not bound: it must re-read on the view model's notifications
  // (Refresh raises them with an empty name) and save only the kinds it changed.
  const build = pageCode.match(/private SshAlgorithmPicker BuildSshAlgorithms\(\)[\s\S]*?\n    }/)?.[0] ?? "";
  assert.match(build, /ViewModel\.PropertyChanged \+=/);
  assert.match(build, /string\.IsNullOrEmpty\(e\.PropertyName\) \|\| e\.PropertyName == nameof\(SettingsViewModel\.SshAlgorithms\)/);
  assert.match(build, /ViewModel\.ChangeSshAlgorithms\(shown, picker\.Value\)/);
  assert.doesNotMatch(build, /ViewModel\.SshAlgorithms = /);
});

const targets = read("src", "App", "ViewModels", "SettingsTargets.cs");
const enumMembers = (name) =>
  (targets.match(new RegExp(String.raw`public enum ${name}\s*\{([\s\S]*?)\}`))?.[1] ?? "")
    .split(",").map(member => member.trim()).filter(Boolean);
const sectionTitles = Object.fromEntries(
  [...targets.matchAll(/SettingsSection\.(\w+) => "([^"]+)"/g)].map(match => [match[1], match[2]])
    .concat([["General", targets.match(/_ => "([^"]+)",\s*\};/)?.[1]]]));

test("every Settings target that is not a section has a field to focus", () => {
  const sections = enumMembers("SettingsSection");
  const fields = pageCode.match(/FieldOf\(GlobalSettingsTarget target\) => target switch[\s\S]*?\n    };/)?.[0] ?? "";
  for (const target of enumMembers("GlobalSettingsTarget").filter(member => !sections.includes(member)))
    assert.match(fields, new RegExp(String.raw`GlobalSettingsTarget\.${target} =>`), target);
});

test("the Settings page shows the catalog's section titles", () => {
  assert.equal(Object.keys(sectionTitles).length, enumMembers("SettingsSection").length);
  for (const [section, title] of Object.entries(sectionTitles)) {
    const xamlTitle = title.replace(/&/g, "&amp;");
    assert.match(page, new RegExp(`Content="${xamlTitle}" Tag="${section}"`), section);
  }
  // Sections with a heading (Sessions is a full-height table without one) repeat the title.
  const headings = [...page.matchAll(/<TextBlock Text="([^"]+)" Style="\{StaticResource SectionTitle\}"/g)]
    .map(match => match[1].replace(/&amp;/g, "&"));
  assert.ok(headings.length >= 7);
  for (const heading of headings)
    assert.ok(Object.values(sectionTitles).includes(heading), heading);
});

test("text that sends people to Settings names a real section, one way", () => {
  const sourceFiles = [];
  const walk = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name);
      if (entry.isDirectory() && !["bin", "obj"].includes(entry.name))
        walk(full);
      else if (/\.(cs|xaml)$/.test(entry.name))
        sourceFiles.push(full);
    }
  };
  walk(path.join(__dirname, "..", "src"));
  const titles = Object.values(sectionTitles);
  const groups = [...page.matchAll(/<TextBlock (?:x:Name="\w+" )?Text="([^"]+)" Style="\{StaticResource GroupHeader\}"/g)]
    .map(match => match[1].replace(/&amp;/g, "&"));
  const sessionSections = [...read("src", "App", "Dialogs", "SessionEditDialog.xaml").matchAll(/<SelectorBarItem [^>]*Text="([^"]+)"/g)]
    .map(match => match[1]);
  for (const file of sourceFiles) {
    const text = fs.readFileSync(file, "utf8").replace(/&amp;/g, "&").replace(/&gt;/g, ">");
    const name = path.relative(path.join(__dirname, ".."), file);
    // In text, not code comments: "Settings > Sessions" in a doc comment names nothing.
    for (const line of text.split("\n").filter(line => !/^\s*\/\//.test(line)))
      assert.doesNotMatch(line, /Settings >/, `${name}: use " › " between Settings sections`);
    for (const match of text.matchAll(/(Session )?Settings › ([^."{}()]+?)(?: › ([^."{}()]+?))?(?=[."{}()]|\s*$|\s+unless|\s+and)/gm)) {
      const [, session, section, group] = match;
      if (session) {
        assert.ok(sessionSections.includes(section.trim()), `${name}: "${match[0]}" names no session editor section`);
        continue;
      }
      assert.ok(titles.includes(section.trim()), `${name}: "${match[0]}" names no Settings section`);
      if (group)
        assert.ok(groups.includes(group.trim()), `${name}: "${match[0]}" names no group in that section`);
    }
  }
});
