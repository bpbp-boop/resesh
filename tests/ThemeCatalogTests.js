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

const themeChrome = fs.readFileSync(path.join(__dirname, "..", "src", "App", "ThemeChrome.cs"), "utf8");

// Which themes exist, and that every table covers each of them, is checked headless by
// AppLogic.Tests/ThemeTableTests; these pin a few deliberate choices.

test("Phthalo Green uses its green shell and terminal palette", () => {
  assert.match(catalog, /new\("phthalo-green", "Phthalo Green"\)/);
  assert.match(
    terminalThemes,
    /\["phthalo-green"\] = New\(\s*0x123524, 0xD7EEE5, 0x72E0AD, 0x245A46,/,
  );
  assert.match(themeChrome, /\["phthalo-green"\] = new\(0x0B2118, 0x2D5A48\)/);
});

test("Crimson pairs a wine-red terminal with a matching tab and a bold red accent", () => {
  assert.match(catalog, /new\("crimson", "Crimson"\)/);
  assert.match(terminalThemes, /\["crimson"\] = New\(\s*0x330B12, 0xF4E4E6,/);
  // The active tab is the terminal background, so tab and terminal read as one surface.
  const crimson = themeChrome.match(/\["crimson"\][\s\S]*?\},/)?.[0] ?? "";
  assert.doesNotMatch(crimson, /ActiveTab/);
  assert.match(crimson, /Accent = 0xFF4D5E,\s*AccentBarThickness = 3/);
  // Unselected crimson tabs stay visibly red instead of fading into the tab strip.
  assert.match(crimson, /InactiveTab = 0x4A101A,\s*HoverTab = 0x5C1622/);
});

test("the app chrome takes background, text and selection from the terminal palette", () => {
  // One copy of each theme's defining colors: the chrome table adds only the strip and divider.
  assert.match(visualPalette, /var terminal = Resesh\.Terminal\.Native\.TerminalThemes\.Find\(id\);/);
  assert.match(visualPalette, /var active = chrome\.ActiveTab \?\? terminal\.Background;/);
  assert.match(visualPalette, /TreeForeground: Hex\(terminal\.Foreground\)/);
  assert.match(visualPalette, /TreeSelection: Hex\(terminal\.Selection\)/);
  assert.doesNotMatch(themeChrome, /\bForeground = |Selection = /);
});

test("the active tab and the focused pane are a full step, not a nudge", () => {
  // Active tab: its own background step plus the theme accent bar above it.
  assert.match(presentation, /FocusedTabBorderColor[\s\S]*?parsed\.A > 0 \? parsed : palette\.Accent/);
  assert.match(presentation, /FocusedTabAccentThickness[\s\S]*?For\(appTheme\)\.AccentBarThickness/);
  assert.match(
    tabGroupXaml,
    /Height="\{x:Bind local:PresentationValues\.FocusedTabAccentThickness\(TabTheme\), Mode=OneWay\}"/,
  );
  // Gutters stay neutral: pane gutters take PaneBorder, the tree splitter Divider.
  assert.match(mainWindow, /Width = isColumns \? 1 : double\.NaN/);
  assert.match(mainWindow, /_paneGutters\.Add\(splitterLine\)/);
  assert.match(mainWindow, /_paneGutters\.Contains\(line\) \? _themePalette\.PaneBorder : _themePalette\.Divider/);
  assert.match(mainWindow, /_paneGutters\.Clear\(\)/);
});

test("the focused pane is outlined like a folder open at its selected tab", () => {
  // Sides and bottom are drawn over the shared one-pixel gutters, from the top of the
  // pane's content (not the tab strip) down, so a seam never doubles.
  assert.match(mainWindowXaml, /<Canvas x:Name="PaneFocusFrame" Grid\.Column="3" IsHitTestVisible="False">/);
  assert.match(mainWindow, /var top = pane\.Y \+ focused!\.ContentEdgeTop;/);
  assert.match(mainWindow, /var leftX = gutterLeft \? pane\.X - 1 : pane\.X;/);
  assert.match(mainWindow, /PlaceFrameEdge\(PaneFocusLeft, gutterLeft \|\| themed, leftX, top, 1, bottomY \+ 1 - top\)/);
  assert.match(mainWindow, /PlaceFrameEdge\(PaneFocusRight, gutterRight \|\| themed, rightX, top, 1, bottomY \+ 1 - top\)/);
  assert.match(mainWindow, /PlaceFrameEdge\(PaneFocusBottom, gutterBelow \|\| themed,/);
  // The top is the tab strip's own bottom line, which already leaves the selected tab open.
  assert.match(tabGroup, /internal void SetFocusEdge\(Windows\.UI\.Color\? color\)/);
  assert.match(tabGroup, /var edge = _showsFocusEdge \? _focusEdgeBrush : _tabDividerBrush;\s*LeftTabStripDivider\.Fill = edge;\s*RightTabStripDivider\.Fill = edge;/);
  assert.match(mainWindow, /groupView\.SetFocusEdge\(ReferenceEquals\(groupView, focused\) \? color : null\)/);
  // Drawn when split, or always (every side) for a session themed unlike the app,
  // in that session's theme.
  assert.match(mainWindow, /var themed = sessionPalette is not null && sessionPalette != _themePalette;/);
  assert.match(mainWindow, /var focused = \(ViewModel\.IsSplit \|\| themed\)\s*&& _groupViews\.TryGetValue\(ViewModel\.FocusedGroup, out var view\)/);
  assert.match(mainWindow, /var color = \(sessionPalette \?\? _themePalette\)\.PaneFocusBorder;/);
  // It follows focus, theme, session edits, resizes and layout rebuilds.
  assert.match(mainWindow, /e\.PropertyName == nameof\(MainViewModel\.ActiveSessionTheme\)\)\s*\{\s*UpdatePaneFocusFrame\(\)/);
  assert.match(mainWindow, /private void RepaintSplitterLines\(\)[\s\S]*?UpdatePaneFocusFrame\(\);/);
  assert.match(mainWindow, /view\.SizeChanged \+= \(_, _\) => UpdatePaneFocusFrame\(\)/);
  assert.match(mainWindow, /DispatcherQueuePriority\.Low, UpdatePaneFocusFrame\)/);
});

test("the command palette is built from theme surfaces, not Fluent defaults", () => {
  assert.doesNotMatch(commandPalette, /ThemeResource (?:SolidBackgroundFillColorBaseBrush|SurfaceStrokeColorDefaultBrush)/);
  // Card on the shell surface, search field recessed to the input surface.
  assert.match(commandPalette, /x:Name="PaletteCard"[\s\S]*?Background="\{StaticResource SessionShellBrush\}"[\s\S]*?BorderBrush="\{StaticResource SessionChromeFrameBrush\}"/);
  const searchBox = commandPalette.match(/<TextBox\s+x:Name="SearchBox"[\s\S]*?<\/TextBox>/)?.[0] ?? "";
  assert.match(searchBox, /x:Key="TextControlBackground" ResourceKey="SessionInputBrush"/);
  assert.match(searchBox, /x:Key="TextControlPlaceholderForeground" ResourceKey="SessionPlaceholderForegroundBrush"/);
  // Focus is the one place the palette spends the theme accent.
  assert.match(searchBox, /x:Key="TextControlBorderBrushFocused" ResourceKey="SessionAccentBrush"/);
  // Rows select with the session tree's selection color.
  assert.match(commandPalette, /x:Key="ListViewItemBackgroundSelected" ResourceKey="SessionTreeSelectionBrush"/);
  assert.match(commandPalette, /x:Key="ListViewItemForegroundSelected" ResourceKey="SessionTreeSelectionForegroundBrush"/);
  assert.match(appXaml, /x:Key="SessionAccentBrush"/);
  assert.match(visualPalette, /Set\("SessionAccentBrush", Accent\)/);
});

test("the command palette composes above terminal surfaces without visibility workarounds", () => {
  assert.match(
    mainWindow,
    /void ShowCommandPalette\(\)[\s\S]*?CommandPalette\.Open\(commands\)/,
  );
  assert.match(
    mainWindow,
    /CloseCommandPalette\(bool restoreFocus\)[\s\S]*?CommandPalette\.Close\(\);[\s\S]*?RestoreOverlayFocus\(focus\)/,
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
  // Every form section scrolls, and the scrollbar has its own gutter rather than
  // sitting on the fields.
  const sections = [
    [sessionEditXaml, "ConnectionPanel"],
    [sessionEditXaml, "NetworkPanel"],
    [sessionEditXaml, "TerminalPanel"],
    [sessionEditXaml, "AppearancePanel"],
    [localDialogXaml, "ShellPanel"],
    [localDialogXaml, "AppearancePanel"],
  ];
  for (const [xaml, name] of sections) {
    const section = xaml.match(new RegExp(`x:Name="${name}"[\\s\\S]*?>`))?.[0] ?? "";
    assert.match(section, /VerticalScrollBarVisibility="Auto"/, name);
    assert.match(section, /Padding="0,0,12,0"/, name);
  }
  // The local profile editor shares the session editor's tabbed shape and width.
  assert.match(localDialogXaml, /<x:Double x:Key="ContentDialogMaxWidth">660<\/x:Double>/);
  assert.match(localDialogXaml, /<StackPanel x:Name="ProfileForm" Spacing="12" Width="600">/);
  assert.match(localDialogXaml, /<SelectorBar x:Name="SectionBar"/);
  // Both fit their section host to the measured dialog chrome, so the dialog itself never
  // scrolls on top of the section.
  assert.match(sessionDialog, /DialogSizing\.FitSections\(this, SessionForm, SectionHost/);
  assert.match(localDialog, /DialogSizing\.FitSections\(this, ProfileForm, SectionHost/);
  // No negative margins faking the gap between a heading and its caption.
  assert.doesNotMatch(sessionEditXaml, /Margin="0,-\d/);
});

test("the status bar takes a themed chrome surface, not a translucent Fluent layer", () => {
  assert.match(appXaml, /x:Key="SessionChromeBrush"/);
  assert.match(mainWindowXaml, /x:Name="StatusBar"[\s\S]*?Background="\{StaticResource SessionChromeBrush\}"/);
  assert.match(visualPalette, /Set\("SessionChromeBrush", Chrome\)/);
});

test("global and per-session theme pickers use the shared catalog", () => {
  assert.match(settingsViewModel, /List<ThemeChoice> Themes \{ get; \} = \[\.\. ThemeCatalog\.All\]/);
  assert.match(settingsPage, /ItemsSource="\{x:Bind ViewModel\.Themes\}"/);
  // Both session editors show the one shared Appearance form.
  assert.match(fs.readFileSync(path.join(__dirname, "..", "src", "App", "Controls", "SessionAppearanceForm.xaml.cs"), "utf8"), /Concat\(ThemeCatalog\.All\)/);
  assert.match(sessionEditXaml, /<controls:SessionAppearanceForm x:Name="AppearanceForm"/);
  assert.match(localDialogXaml, /<controls:SessionAppearanceForm x:Name="AppearanceForm"/);
});

test("global theme selection applies immediately in every window", () => {
  assert.match(settingsPage, /SelectedItem="\{x:Bind ViewModel\.Theme, Mode=TwoWay\}"/);
  // Each window follows the settings store itself, whichever window saved the theme.
  assert.match(mainWindow, /App\.Settings\.Changed \+= Settings_Changed;/);
  assert.match(mainWindow, /private void ApplyThemeToApp\(string theme\)/);
});

test("live theme changes avoid terminal layout and highlight work", () => {
  // A theme change is SettingsEffect.Theme alone (SettingsEffectsTests), which only recolors.
  assert.match(mainWindow, /if \(effect\.HasFlag\(SettingsEffect\.Theme\)\)\s*ApplyThemeToApp\(settings\.Theme\);/);
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
  assert.match(catalog, /\?\? DefaultChoice;/);
  assert.match(terminalThemes, /Themes\.TryGetValue\(id, out var theme\) \? theme : Themes\[DefaultId\]/);
  assert.match(themeChrome, /ById\.TryGetValue\(id, out var chrome\) \? chrome : ById\[ThemeIds\.Default\]/);
});

test("custom themes recolor the app shell and tab strip", () => {
  assert.match(visualPalette, /Set\("SessionShellBrush", Shell\)/);
  assert.match(visualPalette, /Set\("SessionInputBrush", Input\)/);
  assert.doesNotMatch(mainWindowXaml, /x:Name="(?:Expand|Collapse)AllButton"[^>]*?Background=/);
  assert.match(appXaml, /x:Key="SessionShellBrush"/);
  assert.match(appXaml, /x:Key="SessionInputBrush"/);
  assert.match(appXaml, /x:Key="SessionChromeFrameBrush"/);
  assert.match(mainWindow, /groupView\.ApplyTheme\(palette\)/);
  assert.match(tabGroup, /Tabs\.Background = background/);
  assert.match(tabGroup, /TabStripActions\.Background = background/);
  assert.match(tabGroup, /Resources\["TabViewBorderBrush"\] = divider/);
});

test("live theme changes recolor session-tree labels, details, icons, and selection", () => {
  // App scope, so the Welcome tab shares the same mutable brush instances.
  assert.match(appXaml, /x:Key="SessionTreeForegroundBrush"/);
  assert.match(appXaml, /x:Key="SessionTreeMutedForegroundBrush"/);
  assert.match(mainWindowXaml, /x:Name="SessionTree"[\s\S]*?Foreground="\{StaticResource SessionTreeForegroundBrush\}"/);
  assert.match(mainWindowXaml, /Text="\{x:Bind HostSummary, Mode=OneWay\}"[\s\S]*?PresentationValues\.TreeMutedForeground\(IsSelected\)/);
  assert.match(visualPalette, /Set\("SessionTreeForegroundBrush", TreeForeground\)/);
  assert.match(visualPalette, /Set\("SessionTreeMutedForegroundBrush", TreeMutedForeground\)/);
  assert.match(visualPalette, /Set\("SessionTreeSelectionBrush", TreeSelection\)/);
  assert.match(visualPalette, /Set\("SessionTreeSelectionForegroundBrush", TreeSelectionForeground\)/);
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
  assert.match(mainWindow, /highContrastChanged \|\| ThemeCatalog\.IsSystem\(theme\)/);
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

// The ruler's edge used to be drawn in the selection colour, which is also the focused
// pane's outline, so the annotations lane read as another split. The lane is now a faint
// lift of the terminal background toward its foreground, with an edge only in High Contrast.
test("the terminal ruler is a faintly tinted lane, not a bordered one", () => {
  const ruler = fs.readFileSync(
    path.join(__dirname, "..", "src", "Terminal", "Native", "TerminalRuler.cs"),
    "utf8");
  assert.match(terminalSurface, /_ruler\.SetTheme\(dark: [^,]+, _theme\.Background, _theme\.Foreground\);/);
  assert.doesNotMatch(terminalSurface, /_ruler\.SetTheme\([^)]*Selection/);
  const tint = Number(ruler.match(/internal const double LaneTint = (\d*\.\d+);/)?.[1]);
  assert.ok(tint > 0 && tint <= 0.08, "the lane stays a faint lift, not a panel");
  assert.match(ruler, /: Over\(Rgb\(_laneForeground\), LaneTint, _laneBackground\);/);
  assert.match(ruler, /var edge = highContrast \? Math\.Max\(1, \(int\)Math\.Round\(scale\)\) : 0;/);
});

test("live theme changes repaint existing shell and pane dividers", () => {
  assert.match(mainWindowXaml, /x:Name="TitleBarDivider"/);
  assert.match(mainWindowXaml, /x:Name="StatusBar"/);
  assert.match(visualPalette, /Set\("SessionChromeFrameBrush", Frame\)/);
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
  // The strip edge reuses the retained divider brush unless it is drawing pane focus.
  assert.match(tabGroup, /internal void ApplyTheme\(ThemeVisualPalette palette\)[\s\S]*?ApplyStripEdge\(\);/);
  assert.match(tabGroup, /LeftTabStripDivider\.Fill = edge/);
  assert.match(tabGroup, /RightTabStripDivider\.Fill = edge/);
});

test("resesh Dark keeps its original divider and bypasses stale Fluent strokes", () => {
  assert.match(themeChrome, /\[ThemeIds\.Dark\] = new\(0x181818, 0x2B2B2B\)/);
  assert.match(terminalThemes, /\["dark"\] = New\(\s*0x0C0C0C,/);
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

test("placeholder text is a step dimmer than captions, app-wide and in dialogs", () => {
  // Fluent's secondary text color sits too close to typed text to read as a hint.
  assert.match(visualPalette, /Placeholder => IsHighContrast\s*\? TreeForeground\s*: Mix\(TreeForeground, Input, 0\.45\)/);
  assert.match(visualPalette, /Set\("SessionPlaceholderForegroundBrush", Placeholder\)/);
  for (const key of ["TextControlPlaceholderForeground", "TextControlPlaceholderForegroundFocused", "ComboBoxPlaceHolderForeground"]) {
    assert.match(appXaml, new RegExp(`x:Key="${key}" ResourceKey="SessionPlaceholderForegroundBrush"`), key);
  }
  assert.match(dialogTheme, /Set\(dialog, placeholder,[\s\S]*?"TextControlPlaceholderForeground"/);
  const appSource = fs.readdirSync(path.join(__dirname, "..", "src", "App"), { recursive: true })
    .filter(name => name.endsWith(".xaml") && !/^(?:bin|obj)[\\/]/.test(name))
    .map(name => fs.readFileSync(path.join(__dirname, "..", "src", "App", name), "utf8"))
    .join("\n");
  assert.doesNotMatch(appSource, /Placeholder\w*="\{StaticResource SessionTreeMutedForegroundBrush\}"/);
  assert.doesNotMatch(appSource, /x:Key="TextControlPlaceholderForeground\w*" ResourceKey="SessionTreeMutedForegroundBrush"/);
});
