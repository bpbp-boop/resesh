const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const catalog = fs.readFileSync(path.join(__dirname, "..", "src", "Core", "Storage", "ThemeCatalog.cs"), "utf8");
const terminalThemes = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "Native", "TerminalThemes.cs"), "utf8");
const terminalSurface = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "Native", "NativeTerminalSurface.cs"), "utf8");
const settingsPage = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Controls", "SettingsPage.xaml"), "utf8");
const settingsViewModel = fs.readFileSync(path.join(__dirname, "..", "src", "App", "ViewModels", "SettingsViewModel.cs"), "utf8");
const appCode = fs.readFileSync(path.join(__dirname, "..", "src", "App", "App.xaml.cs"), "utf8");
const sessionDialog = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "SessionEditDialog.xaml.cs"), "utf8");
const localDialog = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "LocalProfileEditDialog.xaml.cs"), "utf8");
const localDialogXaml = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "LocalProfileEditDialog.xaml"), "utf8");
const mainWindow = require("./support/mainWindowSource");
const mainWindowXaml = fs.readFileSync(path.join(__dirname, "..", "src", "App", "MainWindow.xaml"), "utf8");
const appXaml = fs.readFileSync(path.join(__dirname, "..", "src", "App", "App.xaml"), "utf8");
const tabGroup = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml.cs"), "utf8");
const tabGroupXaml = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml"), "utf8");
const terminalTab = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"), "utf8");
const dialogTheme = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "DialogTheme.cs"), "utf8");
const sessionEditXaml = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "SessionEditDialog.xaml"), "utf8");
const commandPalette = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Controls", "CommandPaletteView.xaml"), "utf8");
const presentation = fs.readFileSync(path.join(__dirname, "..", "src", "App", "PresentationValues.cs"), "utf8");
const visualPalette = fs.readFileSync(path.join(__dirname, "..", "src", "App", "ThemeVisualPalette.cs"), "utf8");

const ids = [...catalog.matchAll(/new\("([a-z-]+)",/g)].map(match => match[1]);
function terminalPalette(id) {
  // Catalog ids are [a-z-] only, so they need no regex escaping.
  const body = terminalThemes.match(new RegExp(`\\["${id}"\\] = New\\(([^)]*)\\)`))?.[1];
  assert.ok(body, `terminal palette ${id}`);
  return body.match(/0x[0-9A-F]{6}\b/g) ?? [];
}

test("every terminal palette defines all twenty colors", () => {
  for (const id of ids) assert.equal(terminalPalette(id).length, 20, id);
});

test("each catalog theme has a terminal palette", () => {
  for (const id of ids) assert.ok(terminalThemes.includes(`["${id}"] = New(`), id);
});

test("Phthalo Green uses its green shell and terminal palette", () => {
  assert.match(catalog, /new\("phthalo-green", "Phthalo Green"\)/);
  assert.match(
    terminalThemes,
    /\["phthalo-green"\] = New\(\s*0x123524, 0xD7EEE5, 0x72E0AD, 0x245A46,/,
  );
  assert.match(visualPalette, /"phthalo-green" => New\(0x123524, 0x0B2118, 0x2D5A48, 0xD7EEE5, 0x245A46\)/);
});

test("the active tab and the focused pane are a full step, not a nudge", () => {
  // Active tab: its own background step plus the theme accent bar above it.
  assert.match(presentation, /FocusedTabBorderColor[\s\S]*?parsed\.A > 0 \? parsed : palette\.Accent/);
  assert.match(presentation, /FocusedTabAccentThickness[\s\S]*?For\(appTheme\)\.AccentBarThickness/);
  assert.match(
    tabGroupXaml,
    /Height="\{x:Bind local:PresentationValues\.FocusedTabAccentThickness\(AppTheme\), Mode=OneWay\}"/,
  );
  // Focused pane: the gutter the two panes already share IS the border, so the seam
  // between panes stays exactly one pixel instead of stacking a border on each side.
  assert.doesNotMatch(tabGroupXaml, /PaneFocusEdge/);
  assert.match(
    mainWindow,
    /groups\.Contains\(ViewModel\.FocusedGroup\) \? _themePalette\.PaneFocusBorder : _themePalette\.PaneBorder/,
  );
  assert.match(mainWindow, /Width = isColumns \? 1 : double\.NaN/);
  assert.match(
    mainWindow,
    /_paneBoundaries\[splitterLine\] =\s*\[\.\. branch\.Children\[index\]\.Values, \.\. branch\.Children\[index \+ 1\]\.Values\]/,
  );
  // A boundary the focused pane does not touch stays neutral, and so does the tree splitter.
  assert.match(mainWindow, /_paneBoundaries\.TryGetValue\(line, out var groups\)[\s\S]*?_themePalette\.Divider/);
  assert.match(mainWindow, /_paneBoundaries\.Clear\(\)/);
});

test("the command palette is built from theme surfaces, not Fluent defaults", () => {
  assert.doesNotMatch(commandPalette, /ThemeResource (?:SolidBackgroundFillColorBaseBrush|SurfaceStrokeColorDefaultBrush)/);
  // Card on the shell surface, search field recessed to the input surface.
  assert.match(commandPalette, /x:Name="PaletteCard"[\s\S]*?Background="\{StaticResource SessionShellBrush\}"[\s\S]*?BorderBrush="\{StaticResource SessionChromeFrameBrush\}"/);
  const searchBox = commandPalette.match(/<TextBox\s+x:Name="SearchBox"[\s\S]*?<\/TextBox>/)?.[0] ?? "";
  assert.match(searchBox, /x:Key="TextControlBackground" ResourceKey="SessionInputBrush"/);
  assert.match(searchBox, /x:Key="TextControlPlaceholderForeground" ResourceKey="SessionTreeMutedForegroundBrush"/);
  // Focus is the one place the palette spends the theme accent.
  assert.match(searchBox, /x:Key="TextControlBorderBrushFocused" ResourceKey="SessionAccentBrush"/);
  // Rows select with the session tree's selection color.
  assert.match(commandPalette, /x:Key="ListViewItemBackgroundSelected" ResourceKey="SessionTreeSelectionBrush"/);
  assert.match(commandPalette, /x:Key="ListViewItemForegroundSelected" ResourceKey="SessionTreeSelectionForegroundBrush"/);
  assert.match(appXaml, /x:Key="SessionAccentBrush"/);
  assert.match(mainWindow, /SessionAccentBrush"\]\)\.Color = palette\.Accent/);
});

test("the command palette composes above terminal surfaces without visibility workarounds", () => {
  assert.match(
    mainWindow,
    /ShowCommandPalette\(bool openedFromTerminal = false\)[\s\S]*?CommandPalette\.Open\(commands\)/,
  );
  assert.match(
    mainWindow,
    /CloseCommandPalette\(\)[\s\S]*?CommandPalette\.Close\(\);[\s\S]*?RestorePaletteFocus\(\)/,
  );
  assert.doesNotMatch(mainWindow, /SetTerminalHostsVisible/);
});

test("dialogs follow the live session palette and light-dark mode", () => {
  // Dialogs open in their own popup root, so the palette and RequestedTheme have to be
  // pushed onto them instead of relying on the window's element theme.
  assert.match(sessionDialog, /InitializeComponent\(\);\s*DialogTheme\.Apply\(this\)/);
  assert.match(localDialog, /DialogTheme\.Apply\(this\)/);
  // Settings is a page in the window, so it takes the window's theme and palette brushes.
  assert.match(settingsPage, /Background="\{StaticResource SessionShellBrush\}"/);
  assert.match(settingsPage, /x:Key="SettingsCardBackground" ResourceKey="SettingsCardBackgroundBrush"/);
  // Surfaces, fields, and selection reuse the same brushes as the main window.
  assert.match(dialogTheme, /Set\(dialog, shell,[\s\S]*?"ContentDialogBackground"/);
  assert.match(dialogTheme, /Set\(dialog, input,[\s\S]*?"TextControlBackground"[\s\S]*?"ComboBoxBackground"/);
  // Buttons keep the native Fluent fill and elevation border; only text and accent are themed.
  assert.doesNotMatch(dialogTheme, /"Button(?:Background|BorderBrush)/);
  assert.match(dialogTheme, /Set\(dialog, selection,[\s\S]*?"ComboBoxItemBackgroundSelected"/);
  assert.match(dialogTheme, /Set\(dialog, accent,[\s\S]*?"TextControlBorderBrushFocused"[\s\S]*?"AccentFillColorDefaultBrush"/);
  assert.match(
    dialogTheme,
    /dialog\.RequestedTheme = ThemeCatalog\.IsLight\(App\.ResolveTheme\(theme\)\)[\s\S]*?\? ElementTheme\.Light[\s\S]*?: ElementTheme\.Dark/,
  );
  // Mutable app brushes, so a live theme change reaches an open dialog.
  assert.match(dialogTheme, /private static Brush Brush\(string key\) =>\s*\(Brush\)Application\.Current\.Resources\[key\]/);
});

test("the session options form has room for its columns and scrolls every section", () => {
  // 500 of content inside the stock 548 cap left nothing for the dialog's own padding.
  assert.match(sessionEditXaml, /<x:Double x:Key="ContentDialogMaxWidth">660<\/x:Double>/);
  assert.match(sessionEditXaml, /<StackPanel x:Name="SessionForm" Spacing="12" Width="600">/);
  // Both form sections scroll, and the scrollbar has its own gutter rather than
  // sitting on the fields.
  const connection = sessionEditXaml.match(/x:Name="ConnectionPanel"[\s\S]*?>/)?.[0] ?? "";
  const terminal = sessionEditXaml.match(/x:Name="TerminalPanel"[\s\S]*?>/)?.[0] ?? "";
  for (const section of [connection, terminal]) {
    assert.match(section, /VerticalScrollBarVisibility="Auto"/);
    assert.match(section, /Padding="0,0,12,0"/);
  }
  assert.match(localDialogXaml, /<ScrollViewer MaxHeight="560" Padding="0,0,12,0"/);
  // No negative margins faking the gap between a heading and its caption.
  assert.doesNotMatch(sessionEditXaml, /Margin="0,-\d/);
});

test("the status bar takes a themed chrome surface, not a translucent Fluent layer", () => {
  assert.match(appXaml, /x:Key="SessionChromeBrush"/);
  assert.match(mainWindowXaml, /x:Name="StatusBar"[\s\S]*?Background="\{StaticResource SessionChromeBrush\}"/);
  assert.match(mainWindow, /SessionChromeBrush"\]\)\.Color = palette\.Chrome/);
});

test("global and per-session theme pickers use the shared catalog", () => {
  assert.match(settingsViewModel, /Themes => ThemeCatalog\.All/);
  assert.match(settingsPage, /ItemsSource="\{x:Bind ViewModel\.Themes\}"/);
  assert.match(sessionDialog, /Concat\(ThemeCatalog\.All\)/);
  assert.match(localDialog, /Concat\(ThemeCatalog\.All\)/);
});

test("global theme selection applies immediately in every window", () => {
  assert.match(settingsPage, /SelectedItem="\{x:Bind ViewModel\.Theme, Mode=TwoWay\}"/);
  assert.match(appCode, /internal static void ApplySettingChange[\s\S]*?foreach \(var window in app\._windows\.ToList\(\)\)[\s\S]*?window\.ApplySettingChange\(property, source\)/);
  assert.match(mainWindow, /private void ApplyThemeToApp\(string theme\)/);
});

test("live theme changes avoid terminal layout and highlight work", () => {
  assert.match(mainWindow, /case nameof\(SettingsViewModel\.Theme\):\s*ApplyThemeToApp\(settings\.Theme\);\s*break;/);
  assert.match(mainWindow, /private void ApplyThemeToApp\(string theme\)[\s\S]*?view\.ApplyTheme\(theme\)/);

  const applyTheme = terminalTab.match(/public void ApplyTheme\(string theme\)\s*\{[\s\S]*?\n    \}/)?.[0];
  assert.ok(applyTheme);
  assert.match(applyTheme, /_terminal\.ApplyOptions\(theme:/);
  assert.doesNotMatch(applyTheme, /ApplyHighlights|fontSize|fontFamily|scrollback/);

  assert.match(mainWindow, /private void ApplyThemePalette\(string theme\)[\s\S]*?view\.ApplyTheme\(theme\)/);
});

test("light-dark changes commit custom colors in the framework composition frame", () => {
  assert.match(
    mainWindow,
    /CompositionTarget\.Rendering \+= ApplyPaletteBeforeRender;[\s\S]*?Root\.RequestedTheme = requestedTheme/,
  );
  assert.match(
    mainWindow,
    /CompositionTarget\.Rendering -= ApplyPaletteBeforeRender;[\s\S]*?version == _themeApplyVersion\)[\s\S]*?ApplyThemePalette\(theme\)/,
  );
  assert.match(
    mainWindow,
    /if \(Root\.RequestedTheme == requestedTheme\)[\s\S]*?ApplyThemePalette\(theme\);[\s\S]*?return;/,
  );
  const groupApplyTheme =
    tabGroup.match(/internal void ApplyTheme\(ThemeVisualPalette palette\)\s*\{[\s\S]*?\n    \}/)?.[0] ?? "";
  assert.doesNotMatch(groupApplyTheme, /QueueTabTemplateRefresh/);
});

test("unknown saved theme identifiers fall back safely", () => {
  assert.match(catalog, /\?\? All\[0\]/);
  assert.match(terminalThemes, /Themes\.TryGetValue\(id, out var theme\) \? theme : Themes\["dark"\]/);
});

test("custom themes recolor the app shell and tab strip", () => {
  assert.match(mainWindow, /SessionShellBrush"\]\)\.Color = palette\.Shell/);
  assert.match(mainWindow, /SessionInputBrush"\]\)\.Color = palette\.Input/);
  assert.doesNotMatch(mainWindowXaml, /x:Name="(?:Expand|Collapse)AllButton"[^>]*?Background=/);
  assert.match(appXaml, /x:Key="SessionShellBrush"/);
  assert.match(appXaml, /x:Key="SessionInputBrush"/);
  assert.match(appXaml, /x:Key="SessionChromeFrameBrush"/);
  assert.match(mainWindow, /groupView\.ApplyTheme\(palette\)/);
  assert.match(tabGroup, /Tabs\.Background = background/);
  assert.match(tabGroup, /TabStripActions\.Background = background/);
  assert.match(tabGroup, /Resources\["TabViewBorderBrush"\] = divider/);
});

test("each theme gives the session tree its terminal foreground and selection colors", () => {
  const expected = {
    light: ["383A42", "BFCEFF"],
    "solarized-dark": ["839496", "274852"],
    "solarized-light": ["657B83", "EEE8D5"],
    dracula: ["F8F8F2", "44475A"],
    "one-dark": ["ABB2BF", "3E4451"],
    nord: ["D8DEE9", "434C5E"],
    "gruvbox-dark": ["EBDBB2", "504945"],
    monokai: ["F8F8F2", "49483E"],
    "tokyo-night": ["C0CAF5", "33467C"],
    "catppuccin-mocha": ["CDD6F4", "45475A"],
    "phthalo-green": ["D7EEE5", "245A46"],
  };

  for (const [id, [foreground, selection]] of Object.entries(expected)) {
    assert.match(
      visualPalette,
      new RegExp(`"${id}"\\s*=>\\s*New\\([^\\n]*0x${foreground},\\s*0x${selection}\\)`),
      id,
    );
  }
  assert.match(visualPalette, /_ => New\([^\n]*0xCCCCCC, 0x264F78\)/);
});

test("live theme changes recolor session-tree labels, details, icons, and selection", () => {
  // App scope, so the Welcome tab shares the same mutable brush instances.
  assert.match(appXaml, /x:Key="SessionTreeForegroundBrush"/);
  assert.match(appXaml, /x:Key="SessionTreeMutedForegroundBrush"/);
  assert.match(mainWindowXaml, /x:Name="SessionTree"[\s\S]*?Foreground="\{StaticResource SessionTreeForegroundBrush\}"/);
  assert.match(mainWindowXaml, /Text="\{x:Bind HostSummary, Mode=OneWay\}"[\s\S]*?PresentationValues\.TreeMutedForeground\(IsSelected\)/);
  assert.match(mainWindow, /SessionTreeForegroundBrush"\]\)\.Color = palette\.TreeForeground/);
  assert.match(mainWindow, /SessionTreeMutedForegroundBrush"\]\)\.Color = palette\.TreeMutedForeground/);
  assert.match(mainWindow, /SessionTreeSelectionBrush"\]\)\.Color = palette\.TreeSelection/);
  assert.match(mainWindow, /SessionTreeSelectionForegroundBrush"\]\)\.Color = palette\.TreeSelectionForeground/);
});

test("High Contrast uses Windows system colors across custom chrome", () => {
  assert.match(appXaml, /x:Key="HighContrast"/);
  assert.match(appXaml, /SystemColorWindowColor/);
  assert.match(appXaml, /SystemColorWindowTextColor/);
  assert.match(appXaml, /SystemColorHighlightColor/);
  assert.match(tabGroupXaml, /x:Key="HighContrast"[\s\S]*?SystemColorHighlightTextColor/);
  assert.match(visualPalette, /if \(App\.IsHighContrast\)/);
  assert.match(visualPalette, /SystemColorHighlightTextColor/);
  assert.doesNotMatch(mainWindow, /HighContrastChanged \+=/);
  assert.match(mainWindow, /var highContrastChanged = isHighContrast != _isHighContrast/);
  assert.match(mainWindow, /highContrastChanged \|\| string\.Equals\(theme, "system"/);
  assert.match(mainWindow, /ButtonHoverForegroundColor = palette\.TreeSelectionForeground/);
});

test("new split groups start with the live app palette", () => {
  assert.match(mainWindow, /private TabGroupView AttachGroupView[\s\S]*?view\.ApplyTheme\(_themePalette\)/);
});

test("the tab-row divider spans both sides without crossing the active tab", () => {
  assert.match(terminalSurface, /void SetInitialOptions[\s\S]*?_theme = TerminalThemes\.Find\(theme\);[\s\S]*?Background = new SolidColorBrush\(ToColor\(_theme\.Background\)\);/);
  assert.match(terminalThemes, /\["solarized-light"\] = New\(\s*0xFDF6E3,/);
  assert.match(terminalThemes, /\["phthalo-green"\] = New\(\s*0x123524,/);
  assert.match(tabGroupXaml, /x:Name="LeftTabStripDivider"[\s\S]*?x:Name="RightTabStripDivider"/);
  assert.match(tabGroup, /Tabs\.ContainerFromItem\(Tabs\.SelectedItem\)[\s\S]*?LeftTabStripDivider\.Width = activeLeft;[\s\S]*?RightTabStripDivider\.Width = stripWidth - activeRight;/);
});

test("the terminal ruler edge uses a visible theme colour", () => {
  assert.match(terminalSurface, /_ruler\.SetTheme\(dark: [^,]+, _theme\.Background, _theme\.Selection\);/);
  assert.match(terminalThemes, /\["solarized-dark"\] = New\(\s*0x002B36, 0x839496, 0x93A1A1, 0x274852,/);
});

test("live theme changes repaint existing shell and pane dividers", () => {
  assert.match(mainWindowXaml, /x:Name="TitleBarDivider"/);
  assert.match(mainWindowXaml, /x:Name="StatusBar"/);
  assert.match(mainWindow, /SessionChromeFrameBrush"\]\)\.Color = palette\.Frame/);
  assert.match(mainWindowXaml, /x:Name="TitleBarDivider"[\s\S]*?Background="\{StaticResource SessionChromeFrameBrush\}"/);
  assert.match(mainWindowXaml, /x:Name="StatusBar"[\s\S]*?BorderBrush="\{StaticResource SessionChromeFrameBrush\}"/);
  assert.match(mainWindow, /foreach \(var \(splitter, line\) in _splitterLines\)[\s\S]*?line\.Background = SplitterBrush/);
  assert.match(terminalTab, /_chromePalette = ThemeVisualPalette\.For\(theme\);[\s\S]*?ApplyPaneSplitterTheme\(\)/);
  assert.match(terminalTab, /new Microsoft\.UI\.Xaml\.Media\.SolidColorBrush\(_chromePalette\.Divider\)/);
});

test("live theme changes remove WinUI tab insets again after template rebuild", () => {
  assert.match(tabGroup, /private void NormalizeTabStripTemplate\(\)/);
  assert.match(tabGroup, /ActualThemeChanged \+= \(_, _\) => QueueTabTemplateRefresh\(\)/);
  assert.match(tabGroup, /QueueTabTemplateRefresh\(\)/);
  assert.match(tabGroup, /DispatcherQueue\.TryEnqueue\(\(\) => DispatcherQueue\.TryEnqueue/);
});

test("live theme changes recolor both retained tab-row divider brushes", () => {
  assert.match(tabGroup, /_tabDividerBrush\.Color = palette\.Divider/);
  assert.match(tabGroup, /Resources\["TabViewBorderBrush"\] = divider/);
  assert.match(tabGroup, /LeftTabStripDivider\.Fill = divider/);
  assert.match(tabGroup, /RightTabStripDivider\.Fill = divider/);
});

test("resesh Dark keeps its original divider and bypasses stale Fluent strokes", () => {
  assert.match(visualPalette, /_ => New\(0x0C0C0C, 0x181818, 0x2B2B2B,/);
  assert.match(tabGroup, /Resources\["TabViewBorderBrush"\] = divider/);
  assert.match(tabGroup, /ActualThemeChanged \+= \(_, _\) => QueueTabTemplateRefresh\(\)/);
});

test("XAML dialogs opt in to the Fluent ContentDialog style", () => {
  // WinUI does not apply the implicit ContentDialog style to an x:Class subclass, which
  // then falls back to the legacy square template with a flat footer.
  const dialogsDir = path.join(__dirname, "..", "src", "App", "Dialogs");
  const missing = fs.readdirSync(dialogsDir)
    .filter(name => name.endsWith(".xaml"))
    .filter(name => {
      const xaml = fs.readFileSync(path.join(dialogsDir, name), "utf8");
      return /^<ContentDialog\b/m.test(xaml)
        && !/Style="\{StaticResource DefaultContentDialogStyle\}"/.test(xaml);
    });
  assert.deepEqual(missing, []);
});
