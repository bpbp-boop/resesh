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

test("tab colors use the live saved theme instead of the launch-only application theme", () => {
  assert.match(viewModel, /_appTheme = App\.Settings\.Current\.Theme/);
  assert.match(viewModel, /ThemeCatalog\.IsLight\(_appTheme\)/);
  assert.doesNotMatch(viewModel, /Application\.Current\.RequestedTheme/);
});

test("session theme overrides stay inside the terminal and do not recolor tabs", () => {
  const tabVisuals = viewModel.match(/private bool IsDark[\s\S]*?public Microsoft\.UI\.Xaml\.Media\.Brush HeaderForeground/)?.[0] ?? "";
  assert.doesNotMatch(tabVisuals, /WithOverrides|Session\.Overrides/);
  assert.match(tabVisuals, /ThemeVisualPalette\.For\(_appTheme\)/);
});

test("a selected tab in an unfocused split keeps the active surface and gains an underline", () => {
  assert.match(viewModel, /if \(IsActive\)[\s\S]*?color = palette\.ActiveTab/);
  assert.match(viewModel, /InactivePaneUnderlineVisibility[\s\S]*?IsActive && !IsGroupFocused/);
  const xaml = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml"),
    "utf8");
  assert.match(xaml, /Fill="\{x:Bind HeaderBorderBrush, Mode=OneWay\}"[\s\S]*?VerticalAlignment="Bottom"[\s\S]*?InactivePaneUnderlineVisibility/);
});

test("inactive tabs in an unfocused split use a dimmer foreground", () => {
  const foreground = viewModel.match(/public Microsoft\.UI\.Xaml\.Media\.Brush HeaderForeground[\s\S]*?\);/)?.[0] ?? "";
  assert.match(foreground, /: IsGroupFocused/);
  assert.match(foreground, /0x9D[\s\S]*?0x61[\s\S]*?0x72[\s\S]*?0x8A/);
});

test("saved and previewed theme changes refresh every open tab palette", () => {
  assert.match(viewModel, /public void ApplyAppTheme\(string theme\)[\s\S]*?_appTheme = theme;[\s\S]*?NotifyTabVisuals\(\)/);
  assert.match(mainWindow, /private void ApplyThemeToApp\(string theme\)[\s\S]*?foreach \(var tab in ViewModel\.AllTabs\)[\s\S]*?tab\.ApplyAppTheme\(theme\)/);
  assert.match(viewModel, /OnPropertyChanged\(nameof\(HeaderBorderBrush\)\)/);
});
