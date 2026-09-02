const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const catalog = fs.readFileSync(path.join(__dirname, "..", "src", "Core", "Storage", "ThemeCatalog.cs"), "utf8");
const terminal = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "wwwroot", "terminal.html"), "utf8");
const nativeThemes = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "NativeTerminalThemeCatalog.cs"), "utf8");
const nativeSurface = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "NativeTerminalSurface.cs"), "utf8");
const globalDialog = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "GlobalSettingsDialog.cs"), "utf8");
const sessionDialog = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "SessionEditDialog.xaml.cs"), "utf8");
const localDialog = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "LocalProfileEditDialog.cs"), "utf8");
const mainWindow = fs.readFileSync(path.join(__dirname, "..", "src", "App", "MainWindow.xaml.cs"), "utf8");
const mainWindowXaml = fs.readFileSync(path.join(__dirname, "..", "src", "App", "MainWindow.xaml"), "utf8");
const appXaml = fs.readFileSync(path.join(__dirname, "..", "src", "App", "App.xaml"), "utf8");
const tabGroup = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml.cs"), "utf8");
const tabGroupXaml = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml"), "utf8");
const terminalTab = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"), "utf8");
const terminalControl = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "TerminalControl.cs"), "utf8");
const dialogTheme = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "DialogTheme.cs"), "utf8");
const sessionEditXaml = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "SessionEditDialog.xaml"), "utf8");
const commandPalette = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Controls", "CommandPaletteView.xaml"), "utf8");
const presentation = fs.readFileSync(path.join(__dirname, "..", "src", "App", "PresentationValues.cs"), "utf8");
const visualPalette = fs.readFileSync(path.join(__dirname, "..", "src", "App", "ThemeVisualPalette.cs"), "utf8");

const ids = [...catalog.matchAll(/new\("([a-z-]+)",/g)].map(match => match[1]);
const paletteProperties = [
  "background", "foreground", "cursor", "selectionBackground",
  "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
  "brightBlack", "brightRed", "brightGreen", "brightYellow",
  "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
];

function webPalette(id) {
  let body;
  if (id === "dark" || id === "system") {
    body = terminal.match(/const DARK_THEME = \{([\s\S]*?)\n  \};/)?.[1];
  } else if (id === "light") {
    body = terminal.match(/const LIGHT_THEME = \{([\s\S]*?)\n  \};/)?.[1];
  } else {
    const escapedId = id.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    body = terminal.match(new RegExp(`(?:"${escapedId}"|${escapedId})\\s*:\\s*\\{([\\s\\S]*?)\\n\\s*\\}`))?.[1];
  }
  assert.ok(body, `web palette ${id}`);

  return paletteProperties.map(property => {
    const color = body.match(new RegExp(`${property}:\\s*"#([0-9a-f]{6})"`, "i"))?.[1];
    if (id === "dark" || id === "system") {
      if (property === "selectionBackground") return "264F78";
    }
    assert.ok(color, `${id}.${property}`);
    return color.toUpperCase();
  });
}

function nativePalette(id) {
  const escapedId = id.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const body = nativeThemes.match(new RegExp(`\\["${escapedId}"\\]\\s*=\\s*New\\(([\\s\\S]*?)\\),`))?.[1];
  assert.ok(body, `native palette ${id}`);
  const colors = [...body.matchAll(/0x([0-9A-F]{6})/g)].map(match => match[1]);
  assert.equal(colors.length, paletteProperties.length, `${id} native color count`);
  return colors;
}


test("each catalog theme has a terminal palette", () => {
  for (const id of ids) {
    if (id === "dark" || id === "light") continue;
    assert.match(terminal, new RegExp(`(?:"${id}"|${id})\\s*:`), id);
  }
});

test("native palettes exactly match the WebView palettes", () => {
  for (const id of ids) {
    assert.deepEqual(nativePalette(id), webPalette(id), id);
  }
});

test("native font size preserves the WebView CSS-pixel scale", () => {
  assert.equal([...nativeSurface.matchAll(/ToNativePointSize\(_fontSize\)/g)].length, 2);
  assert.match(
    nativeSurface,
    /ToNativePointSize\(int cssPixels\)[\s\S]*?\(cssPixels \* 3 \+ 2\) \/ 4/,
  );
});

test("Phthalo Green uses its green shell and terminal palette", () => {
  assert.match(catalog, /new\("phthalo-green", "Phthalo Green"\)/);
  assert.match(
    terminal,
    /"phthalo-green": \{\s*background: "#123524", foreground: "#d7eee5", cursor: "#72e0ad", selectionBackground: "#245a46"/,
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

test("the command palette hides child terminal windows until it closes", () => {
  assert.match(
    mainWindow,
    /ShowCommandPalette\(bool openedFromTerminal = false\)[\s\S]*?SetTerminalHostsVisible\(false\);[\s\S]*?CommandPalette\.Open\(commands\)/,
  );
  assert.match(
    mainWindow,
    /CloseCommandPalette\(\)[\s\S]*?CommandPalette\.Close\(\);[\s\S]*?SetTerminalHostsVisible\(true\);[\s\S]*?RestorePaletteFocus\(\)/,
  );
  assert.match(
    mainWindow,
    /ExecuteCommandPaletteEntryAsync[\s\S]*?finally[\s\S]*?SetTerminalHostsVisible\(true\)/,
  );
});

test("dialogs follow the live session palette and light-dark mode", () => {
  // Dialogs open in their own popup root, so the palette and RequestedTheme have to be
  // pushed onto them instead of relying on the window's element theme.
  assert.match(sessionDialog, /InitializeComponent\(\);\s*DialogTheme\.Apply\(this\)/);
  assert.match(localDialog, /DialogTheme\.Apply\(this\)/);
  assert.match(globalDialog, /DialogTheme\.Apply\(dialog, PreviewTheme\(\)\)/);
  assert.match(
    globalDialog,
    /theme\.SelectionChanged[\s\S]*?applyThemePreview\(previewTheme\);[\s\S]*?DialogTheme\.SetRequestedTheme\(dialog, previewTheme\)/,
  );
  // Surfaces, fields, and selection reuse the same brushes as the main window.
  assert.match(dialogTheme, /Set\(dialog, shell,[\s\S]*?"ContentDialogBackground"/);
  assert.match(dialogTheme, /Set\(dialog, input,[\s\S]*?"TextControlBackground"[\s\S]*?"ComboBoxBackground"/);
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
  assert.match(sessionEditXaml, /<StackPanel Spacing="12" MinWidth="580" MaxWidth="600">/);
  // Both form sections scroll, and the scrollbar has its own gutter rather than
  // sitting on the fields.
  const connection = sessionEditXaml.match(/x:Name="ConnectionPanel"[\s\S]*?>/)?.[0] ?? "";
  const terminal = sessionEditXaml.match(/x:Name="TerminalPanel"[\s\S]*?>/)?.[0] ?? "";
  for (const section of [connection, terminal]) {
    assert.match(section, /VerticalScrollBarVisibility="Auto"/);
    assert.match(section, /Padding="0,0,12,0"/);
  }
  assert.match(localDialog, /MaxHeight = 560,\s*Padding = new Thickness\(0, 0, 12, 0\)/);
  // No negative margins faking the gap between a heading and its caption.
  assert.doesNotMatch(sessionEditXaml, /Margin="0,-\d/);
});

test("the status bar takes a themed chrome surface, not a translucent Fluent layer", () => {
  assert.match(appXaml, /x:Key="SessionChromeBrush"/);
  assert.match(mainWindowXaml, /x:Name="StatusBar"[\s\S]*?Background="\{StaticResource SessionChromeBrush\}"/);
  assert.match(mainWindow, /SessionChromeBrush"\]\)\.Color = palette\.Chrome/);
});

test("global and per-session theme pickers use the shared catalog", () => {
  assert.match(globalDialog, /ItemsSource = ThemeCatalog\.All/);
  assert.match(sessionDialog, /Concat\(ThemeCatalog\.All\)/);
  assert.match(localDialog, /Concat\(ThemeCatalog\.All\)/);
});

test("global theme selection previews immediately and cancel restores saved theme", () => {
  assert.match(globalDialog, /theme\.SelectionChanged[\s\S]*?applyThemePreview\(previewTheme\)/);
  assert.match(globalDialog, /result = await dialog\.ShowAsync\(\);[\s\S]*?if \(result != ContentDialogResult\.Primary\)[\s\S]*?applyThemePreview\(current\.Theme\)/);
  assert.match(mainWindow, /private void ApplyThemeToApp\(string theme\)/);
});

test("live theme previews avoid terminal layout and highlight work", () => {
  assert.match(mainWindow, /GlobalSettingsDialog\.ShowAsync\([\s\S]*?ApplyThemeToApp, ApplySettingsToApp, target\)/);
  assert.match(mainWindow, /private void ApplyThemeToApp\(string theme\)[\s\S]*?view\.ApplyTheme\(theme\)/);

  const applyTheme = terminalTab.match(/public void ApplyTheme\(string theme\)\s*\{[\s\S]*?\n    \}/)?.[0];
  assert.ok(applyTheme);
  assert.match(applyTheme, /_terminal\.ApplyOptions\(theme:/);
  assert.doesNotMatch(applyTheme, /ApplyHighlights|fontSize|fontFamily|scrollback/);

  assert.match(terminal, /let layoutChanged = false;[\s\S]*?if \(layoutChanged\) \{[\s\S]*?fitPreservingTimestamps\(\)/);
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
  assert.match(terminal, /THEMES\[id\] \|\| DARK_THEME/);
});

test("custom themes recolor the app shell and tab strip", () => {
  assert.match(mainWindow, /SessionShellBrush"\]\)\.Color = palette\.Shell/);
  assert.match(mainWindow, /SessionInputBrush"\]\)\.Color = palette\.Input/);
  assert.match(mainWindowXaml, /x:Name="ExpandAllButton"[\s\S]*?Background="\{StaticResource SessionInputBrush\}"/);
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
  assert.match(terminalControl, /SetInitialOptions[\s\S]*?_webView\.DefaultBackgroundColor = ThemeBackground\(theme\)/);
  assert.match(terminalControl, /"solarized-light" => Windows\.UI\.Color\.FromArgb\(255, 0xFD, 0xF6, 0xE3\)/);
  assert.match(terminalControl, /"phthalo-green" => Windows\.UI\.Color\.FromArgb\(255, 0x12, 0x35, 0x24\)/);
  assert.match(tabGroupXaml, /x:Name="LeftTabStripDivider"[\s\S]*?x:Name="RightTabStripDivider"/);
  assert.match(tabGroup, /Tabs\.ContainerFromItem\(Tabs\.SelectedItem\)[\s\S]*?LeftTabStripDivider\.Width = activeLeft;[\s\S]*?RightTabStripDivider\.Width = stripWidth - activeRight;/);
});

test("the terminal ruler edge uses a visible theme colour", () => {
  assert.match(terminal, /border: theme\.selectionBackground \|\| theme\.brightBlack/);
  assert.match(terminal, /"solarized-dark": \{[\s\S]*?selectionBackground: "#274852"/);
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
