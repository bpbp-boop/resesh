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

test("Welcome draws on the shared app palette instead of stock Fluent surfaces", () => {
  for (const stock of [
    "ApplicationPageBackgroundThemeBrush",
    "LayerFillColorDefaultBrush",
    "SurfaceStrokeColorDefaultBrush",
    "DividerStrokeColorDefaultBrush",
    "ControlFillColorDefaultBrush",
    "ControlStrokeColorDefaultBrush",
    "TextFillColorSecondaryBrush",
  ]) {
    assert.ok(!xaml.includes(stock), `Welcome still uses ${stock}`);
  }

  assert.match(xaml, /<Grid Background="\{StaticResource SessionShellBrush\}">/);
  assert.match(xaml, /Foreground="\{StaticResource SessionTreeForegroundBrush\}">/);
  assert.match(xaml, /Background="\{StaticResource SessionInputBrush\}"/);
  assert.match(xaml, /BorderBrush="\{StaticResource SessionChromeFrameBrush\}"/);
  assert.match(xaml, /Foreground="\{StaticResource SessionTreeMutedForegroundBrush\}"/);
});

test("Welcome buttons and theme cards follow the palette through their states", () => {
  assert.match(
    xaml,
    /<StaticResource x:Key="ButtonBackground" ResourceKey="SessionShellBrush" \/>/);
  assert.match(
    xaml,
    /<StaticResource x:Key="ButtonForegroundDisabled" ResourceKey="SessionTreeMutedForegroundBrush" \/>/);
  assert.match(
    xaml,
    /<StaticResource x:Key="ToggleButtonBackgroundChecked" ResourceKey="SessionInputBrush" \/>/);
  assert.match(
    xaml,
    /<StaticResource x:Key="ToggleButtonForegroundChecked" ResourceKey="SessionTreeForegroundBrush" \/>/);
});

test("Welcome uses native control resources in High Contrast", () => {
  assert.match(xaml, /<ResourceDictionary x:Key="Dark">/);
  assert.match(xaml, /<ResourceDictionary x:Key="HighContrast" \/>/);
  assert.doesNotMatch(xaml, /<ResourceDictionary x:Key="Default">/);
});

test("the palette brushes Welcome shares with the shell live at app scope", () => {
  const appXaml = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "App.xaml"), "utf8");
  assert.match(appXaml, /x:Key="SessionTreeForegroundBrush"/);
  assert.match(appXaml, /x:Key="SessionTreeMutedForegroundBrush"/);

  const mainWindow = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "MainWindow.xaml"), "utf8");
  assert.ok(!/<SolidColorBrush x:Key="SessionTree/.test(mainWindow));

  const mainWindowCode = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "MainWindow.xaml.cs"), "utf8");
  assert.match(
    mainWindowCode,
    /Application\.Current\.Resources\["SessionTreeForegroundBrush"\]/);
  assert.match(
    mainWindowCode,
    /Application\.Current\.Resources\["SessionTreeMutedForegroundBrush"\]/);
});
