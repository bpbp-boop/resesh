const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const viewModel = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "ViewModels", "TabViewModel.cs"),
  "utf8");
const mainWindow = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "MainWindow.xaml.cs"),
  "utf8");
const presentation = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "PresentationValues.cs"),
  "utf8");
const xaml = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml"),
  "utf8");
const visualPalette = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "ThemeVisualPalette.cs"),
  "utf8");

test("tab colors use the live saved theme instead of the launch-only application theme", () => {
  assert.match(viewModel, /_appTheme = App\.Settings\.Current\.Theme/);
  assert.match(presentation, /ThemeCatalog\.IsLight\(appTheme\)/);
  assert.doesNotMatch(viewModel, /Application\.Current\.RequestedTheme/);
});

test("session theme overrides stay inside the terminal and do not recolor tabs", () => {
  const tabVisuals = presentation.match(/public static Windows\.UI\.Color TabHeaderBackgroundColor[\s\S]*?public static Windows\.UI\.Color TabHeaderForegroundColor/)?.[0] ?? "";
  assert.doesNotMatch(tabVisuals, /WithOverrides|Session\.Overrides/);
  assert.match(tabVisuals, /ThemeVisualPalette\.For\(appTheme\)/);
});

test("a selected tab in an unfocused split keeps the active surface and gains an underline", () => {
  assert.match(presentation, /return isActive \? palette\.ActiveTab/);
  assert.match(presentation, /InactiveUnderlineVisibility[\s\S]*?isActive && !isGroupFocused/);
  assert.match(xaml, /VerticalAlignment="Bottom"[\s\S]*?InactiveUnderlineVisibility\(IsActive, IsGroupFocused\)[\s\S]*?TabHeaderBorderColor\(AppTheme\)/);
});

test("a focused selected tab border follows its session color", () => {
  // Session color wins; otherwise the theme's accent, which defaults to the stock blue.
  assert.match(presentation, /FocusedTabBorderColor\(string appTheme, string\? colorTag\)[\s\S]*?parsed\.A > 0 \? parsed : palette\.Accent/);
  assert.match(visualPalette, /Hex\(0x0078D4\), 1, Hex\(divider\), Hex\(selection\), false\)/);
  assert.match(xaml, /FocusedAccentVisibility\(IsActive, IsGroupFocused\)[\s\S]*?FocusedTabBorderColor\(AppTheme, ColorTag\)/);
});

test("inactive tabs in an unfocused split use a dimmer foreground", () => {
  const foreground = presentation.match(/TabHeaderForegroundColor[\s\S]*?TabHeaderFontWeight/)?.[0] ?? "";
  assert.match(foreground, /\? isGroupFocused/);
  assert.match(foreground, /0x9D9D9D[\s\S]*?0x616161[\s\S]*?0x727272[\s\S]*?0x8A8A8A/);
});

test("saved and previewed theme changes refresh every open tab palette", () => {
  assert.match(viewModel, /public void ApplyAppTheme\(string theme\)[\s\S]*?_appTheme = theme;[\s\S]*?OnPropertyChanged\(nameof\(AppTheme\)\)/);
  assert.match(mainWindow, /private void ApplyThemeToApp\(string theme\)[\s\S]*?foreach \(var tab in ViewModel\.AllTabs\)[\s\S]*?tab\.ApplyAppTheme\(theme\)/);
  assert.match(xaml, /TabHeaderBorderColor\(AppTheme\), Mode=OneWay/);
});

test("persistent tabs prefer a newer detected command over a delayed tmux title", () => {
  const subtitle = viewModel.match(
    /public string Subtitle[\s\S]*?private string FallbackSubtitle/)?.[0] ?? "";
  assert.ok(
    subtitle.indexOf("Session.Persistent && RunningCommand") < subtitle.indexOf("TerminalTitle is not"),
    "the detected command must win before the stale tmux title is considered");
  assert.match(
    viewModel,
    /Session\.Persistent && AgentDetection\.IsShellTitle\(TerminalTitle\)[\s\S]*?RunningCommand = null/);
});
