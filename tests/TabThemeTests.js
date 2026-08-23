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

test("a selected tab in an unfocused split uses the inactive tab surface", () => {
  assert.match(viewModel, /if \(IsActive && IsGroupFocused\)[\s\S]*?color = palette\.ActiveTab/);
  assert.match(viewModel, /else[\s\S]*?color = palette\.InactiveTab/);
});

test("saved and previewed theme changes refresh every open tab palette", () => {
  assert.match(viewModel, /public void ApplyAppTheme\(string theme\)[\s\S]*?_appTheme = theme;[\s\S]*?NotifyTabVisuals\(\)/);
  assert.match(mainWindow, /private void ApplyThemeToApp\(string theme\)[\s\S]*?foreach \(var tab in ViewModel\.AllTabs\)[\s\S]*?tab\.ApplyAppTheme\(theme\)/);
  assert.match(viewModel, /OnPropertyChanged\(nameof\(HeaderBorderBrush\)\)/);
});
