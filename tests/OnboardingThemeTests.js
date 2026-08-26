const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const xaml = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "OnboardingView.xaml"),
  "utf8");
const codeBehind = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "OnboardingView.xaml.cs"),
  "utf8");

test("Welcome explains that its theme choice changes the entire app", () => {
  assert.match(xaml, /Text="Application Theme"/);
  assert.match(
    xaml,
    /changes the entire app: menus, session tree, tabs, and terminal colors/);
  assert.match(
    xaml,
    /A theme override for one session changes only that terminal/);
  assert.match(xaml, /x:Name="LightThemeToggle"/);
  assert.match(xaml, /x:Name="DarkThemeToggle"/);
  assert.match(xaml, /x:Name="SystemThemeToggle"/);
});

test("Welcome exposes the full shared theme catalog beside its preview cards", () => {
  assert.match(xaml, /Text="Featured themes"/);
  assert.match(xaml, /Content="Browse all themes\.\.\."/);
  assert.match(xaml, /<MenuFlyout x:Name="AllThemesFlyout"/);
  assert.match(xaml, /x:Name="TokyoNightThemeCard"/);
  assert.match(xaml, /x:Name="PhthaloGreenThemeCard"/);
  assert.match(
    codeBehind,
    /foreach \(var theme in ThemeCatalog\.All\)[\s\S]*?AllThemesFlyout\.Items\.Add\(item\)/);
  assert.match(
    codeBehind,
    /ThemeFlyoutItem_Click[\s\S]*?SelectTheme\(theme\)/);
});

test("the full theme menu tracks the active Welcome theme", () => {
  assert.match(
    codeBehind,
    /foreach \(var item in AllThemesFlyout\.Items\)[\s\S]*?themeMenuItem\.IsChecked = string\.Equals\([\s\S]*?_selectedTheme/);
});
