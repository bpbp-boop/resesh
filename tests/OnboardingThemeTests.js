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
  assert.match(xaml, /Text="Appearance"/);
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

test("Welcome filters the shared theme catalog through one native ComboBox", () => {
  assert.match(xaml, /<ComboBox x:Name="ThemePicker"/);
  assert.match(xaml, /Header="Color theme"/);
  assert.match(xaml, /x:Name="ThemePreviewSurface"/);
  assert.match(codeBehind, /ThemeVisualPalette\.For\(resolved\)/);
  assert.match(
    codeBehind,
    /ThemeCatalog\.All[\s\S]*?\.Where\(theme => theme\.Id != "system" && theme\.IsLight == isLight\)/);
  assert.match(
    codeBehind,
    /ThemePicker_SelectionChanged[\s\S]*?SelectTheme\(theme\.Id\)/);
});

test("Mode has clear ownership of fixed and system theme selection", () => {
  assert.match(
    codeBehind,
    /Mode is a shortcut, not a second persisted setting/);
  assert.match(codeBehind, /ThemePicker\.IsEnabled = false/);
  assert.match(codeBehind, /System follows your Windows color mode/);
});

test("Welcome keeps custom-theme contrast resources local to the page", () => {
  assert.match(xaml, /x:Key="OnboardingPrimaryTextBrush"/);
  assert.match(xaml, /x:Key="OnboardingSecondaryTextBrush"/);
  assert.match(xaml, /x:Key="OnboardingPreviewBorderBrush"/);
  assert.match(codeBehind, /EnsureContrast\(palette\.TreeMutedForeground, palette\.Shell, 4\.5\)/);
  assert.match(codeBehind, /EnsureContrast\(palette\.Frame, palette\.Shell, 3\.0\)/);
});

test("Welcome buttons and theme toggles follow the palette through their states", () => {
  assert.match(
    xaml,
    /<StaticResource x:Key="ButtonBackground" ResourceKey="SessionShellBrush" \/>/);
  assert.match(
    xaml,
    /<StaticResource x:Key="ButtonForegroundDisabled" ResourceKey="OnboardingSecondaryTextBrush" \/>/);
  assert.match(
    xaml,
    /<StaticResource x:Key="ToggleButtonBackgroundChecked" ResourceKey="SessionAccentBrush" \/>/);
  assert.match(
    xaml,
    /<StaticResource x:Key="ToggleButtonForegroundChecked" ResourceKey="OnboardingAccentForegroundBrush" \/>/);
});

test("Welcome uses platform typography, one aligned section grid, and responsive cards", () => {
  assert.doesNotMatch(xaml, /FontSize="(?:[0-9]|1[01])"/);
  assert.match(xaml, /Style="\{ThemeResource TitleTextBlockStyle\}"/);
  assert.equal((xaml.match(/Style="\{ThemeResource SubtitleTextBlockStyle\}"/g) || []).length, 3);
  assert.match(xaml, /<Grid ColumnDefinitions="24,\*" ColumnSpacing="8">/);
  assert.match(xaml, /<AdaptiveTrigger MinWindowWidth="1280" \/>/);
  assert.match(xaml, /<AdaptiveTrigger MinWindowWidth="900" \/>/);
  assert.match(xaml, /x:Name="HeroActions" Orientation="Vertical"/);
  assert.match(xaml, /Target="HeroActions\.Orientation" Value="Horizontal"/);
  assert.match(xaml, /Target="OpenSshImportCard\.\(Grid\.Row\)" Value="0"/);
});

test("Welcome import states and completion behavior are consistent", () => {
  assert.match(codeBehind, /label\.Foreground = Brush\(count > 0[\s\S]*?OnboardingAccentTextBrush[\s\S]*?OnboardingSecondaryTextBrush/);
  assert.match(xaml, /AutomationProperties\.Name="Review PuTTY sessions"/);
  assert.match(xaml, /AutomationProperties\.Name="Review OpenSSH sessions"/);
  assert.match(xaml, /AutomationProperties\.Name="Review SecureCRT sessions"/);
  assert.match(xaml, /x:Name="FinishSetupButton"[\s\S]*?HorizontalAlignment="Right"/);
  assert.match(codeBehind, /FinishSetupButton\.IsEnabled = _savedSettings\.OnboardingCompleted != true/);
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
