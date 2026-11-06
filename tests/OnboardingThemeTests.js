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

test("Welcome appearance choices stay wired to the draft appearance", () => {
  for (const mode of ["System", "Fixed"]) {
    assert.match(xaml, new RegExp(`x:Name="${mode}ThemeToggle"[^>]*Click="${mode}Theme_Click"`));
  }
  assert.match(codeBehind, /SystemTheme_Click[\s\S]*?Appearance = ThemeAppearances\.System/);
  // Leaving System keeps the theme drawn now.
  assert.match(codeBehind, /FixedTheme_Click[\s\S]*?WithFixedTheme\(_choices\.Shown\.ThemeFor\(App\.SystemIsLight\(\)\)\)/);
});

test("Welcome gallery picks the fixed theme, or the theme for Windows' mode under System", () => {
  assert.match(xaml, /<controls:ThemeGallery x:Name="ThemePicker"/);
  assert.match(xaml, /AutomationProperties\.AutomationId="ThemePicker"/);
  assert.match(xaml, /SelectionChanged="ThemePicker_SelectionChanged"/);
  assert.match(codeBehind, /ThemeVisualPalette\.For\(resolved\)/);
  assert.match(codeBehind, /ThemePicker_SelectionChanged[\s\S]*?WithModeTheme\(theme\.Id\)[\s\S]*?WithFixedTheme\(theme\.Id\)/);
  assert.match(codeBehind, /ThemePicker\.Query = ThemeFilter\.Query/);
});

test("Welcome keeps custom-theme contrast resources local to the page", () => {
  assert.match(xaml, /x:Key="OnboardingPrimaryTextBrush"/);
  assert.match(xaml, /x:Key="OnboardingSecondaryTextBrush"/);
  assert.match(xaml, /x:Key="OnboardingPreviewBorderBrush"/);
});

test("Welcome buttons keep the native Fluent surface and theme only text and accent", () => {
  assert.doesNotMatch(xaml, /x:Key="(?:Toggle)?Button(?:Background|BorderBrush)(?:PointerOver|Pressed|Disabled)?"/);
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
  assert.equal((xaml.match(/Style="\{ThemeResource SubtitleTextBlockStyle\}"/g) || []).length, 5);
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
  // Welcome compares with the settings saved now, not a copy taken when it opened.
  assert.match(codeBehind, /FinishSetupButton\.IsEnabled = _choices\.HasChanges\(App\.Settings\.Current\)/);
  assert.match(codeBehind, /_previewTheme\(App\.CurrentTheme\)/);
  assert.doesNotMatch(codeBehind, /_savedSettings/);
  // Settings changed in any window reach an open Welcome, like the Settings pages.
  const windowCode = require("./support/mainWindowSource");
  assert.match(windowCode, /tab\.View is OnboardingView welcome\)\s*welcome\.Refresh\(App\.Settings\.Current\)/);
  assert.match(codeBehind, /App\.Identities\.Changed \+= OnStoreChanged/);
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

  const visualPalette = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "ThemeVisualPalette.cs"), "utf8");
  assert.match(visualPalette, /Set\("SessionTreeForegroundBrush", TreeForeground\)/);
  assert.match(visualPalette, /Set\("SessionTreeMutedForegroundBrush", TreeMutedForeground\)/);
});
