const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const catalog = fs.readFileSync(path.join(__dirname, "..", "src", "Core", "Storage", "ThemeCatalog.cs"), "utf8");
const terminal = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "wwwroot", "terminal.html"), "utf8");
const globalDialog = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "GlobalSettingsDialog.cs"), "utf8");
const sessionDialog = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "SessionEditDialog.xaml.cs"), "utf8");
const localDialog = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Dialogs", "LocalProfileEditDialog.cs"), "utf8");
const mainWindow = fs.readFileSync(path.join(__dirname, "..", "src", "App", "MainWindow.xaml.cs"), "utf8");
const mainWindowXaml = fs.readFileSync(path.join(__dirname, "..", "src", "App", "MainWindow.xaml"), "utf8");
const tabGroup = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml.cs"), "utf8");
const terminalTab = fs.readFileSync(path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"), "utf8");
const terminalControl = fs.readFileSync(path.join(__dirname, "..", "src", "Terminal", "TerminalControl.cs"), "utf8");
const visualPalette = fs.readFileSync(path.join(__dirname, "..", "src", "App", "ThemeVisualPalette.cs"), "utf8");

const ids = [...catalog.matchAll(/new\("([a-z-]+)",/g)].map(match => match[1]);

test("each catalog theme has a terminal palette", () => {
  for (const id of ids) {
    if (id === "dark" || id === "light") continue;
    assert.match(terminal, new RegExp(`(?:"${id}"|${id})\\s*:`), id);
  }
});

test("Phthalo Green uses its green shell and terminal palette", () => {
  assert.match(catalog, /new\("phthalo-green", "Phthalo Green"\)/);
  assert.match(
    terminal,
    /"phthalo-green": \{\s*background: "#123524", foreground: "#d7eee5", cursor: "#72e0ad", selectionBackground: "#245a46"/,
  );
  assert.match(visualPalette, /"phthalo-green" => New\(0x123524, 0x0B2118, 0x2D5A48, 0xD7EEE5, 0x245A46\)/);
});

test("global and per-session theme pickers use the shared catalog", () => {
  assert.match(globalDialog, /ItemsSource = ThemeCatalog\.All/);
  assert.match(sessionDialog, /Concat\(ThemeCatalog\.All\)/);
  assert.match(localDialog, /Concat\(ThemeCatalog\.All\)/);
});

test("global theme selection previews immediately and cancel restores saved settings", () => {
  assert.match(globalDialog, /theme\.SelectionChanged \+= \(_, _\) => applyPreview\(PreviewSettings\(\)\)/);
  assert.match(globalDialog, /if \(await dialog\.ShowAsync\(\) != ContentDialogResult\.Primary\)[\s\S]*?applyPreview\(current\)/);
  assert.match(mainWindow, /private void ApplySettingsToApp\(AppSettings settings\)/);
});

test("unknown saved theme identifiers fall back safely", () => {
  assert.match(catalog, /\?\? All\[0\]/);
  assert.match(terminal, /THEMES\[id\] \|\| DARK_THEME/);
});

test("custom themes recolor the app shell and tab strip", () => {
  assert.match(mainWindow, /Root\.Background = .*palette\.Shell/);
  assert.match(mainWindow, /FilterFieldBorder\.Background = .*palette\.Input/);
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
  assert.match(mainWindowXaml, /x:Key="SessionTreeForegroundBrush"/);
  assert.match(mainWindowXaml, /x:Key="SessionTreeMutedForegroundBrush"/);
  assert.match(mainWindowXaml, /x:Name="SessionTree"[\s\S]*?Foreground="\{StaticResource SessionTreeForegroundBrush\}"/);
  assert.match(mainWindowXaml, /Text="\{x:Bind HostSummary, Mode=OneWay\}"[\s\S]*?Foreground="\{StaticResource SessionTreeMutedForegroundBrush\}"/);
  assert.match(mainWindow, /SessionTreeForegroundBrush"\]\)\.Color = palette\.TreeForeground/);
  assert.match(mainWindow, /SessionTreeMutedForegroundBrush"\]\)\.Color = palette\.TreeMutedForeground/);
  assert.match(mainWindow, /TreeNodeViewModel\.ApplySelectionTheme\(palette\.TreeSelection\)/);
});

test("new split groups start with the live app palette", () => {
  assert.match(mainWindow, /private TabGroupView AttachGroupView[\s\S]*?view\.ApplyTheme\(_themePalette\)/);
});

test("terminal startup and the complete tab-row divider use the selected palette", () => {
  assert.match(terminalControl, /SetInitialOptions[\s\S]*?_webView\.DefaultBackgroundColor = ThemeBackground\(theme\)/);
  assert.match(terminalControl, /"solarized-light" => Windows\.UI\.Color\.FromArgb\(255, 0xFD, 0xF6, 0xE3\)/);
  assert.match(terminalControl, /"phthalo-green" => Windows\.UI\.Color\.FromArgb\(255, 0x12, 0x35, 0x24\)/);
  assert.match(tabGroup, /FindDescendant\(Tabs, "RightBottomBorderLine"\)/);
  assert.match(tabGroup, /right\.Margin = new Thickness\(-1, 0, 0, 0\)/);
  assert.match(tabGroup, /TabStripActions\.BorderBrush = divider/);
});

test("the terminal ruler edge uses a visible theme colour", () => {
  assert.match(terminal, /border: theme\.selectionBackground \|\| theme\.brightBlack/);
  assert.match(terminal, /"solarized-dark": \{[\s\S]*?selectionBackground: "#274852"/);
});

test("live theme changes repaint existing shell and pane dividers", () => {
  assert.match(mainWindowXaml, /x:Name="TitleBarDivider"/);
  assert.match(mainWindowXaml, /x:Name="StatusBar"/);
  assert.match(mainWindow, /TitleBarDivider\.Background = .*palette\.Frame/);
  assert.match(mainWindow, /StatusBar\.BorderBrush = .*palette\.Frame/);
  assert.match(mainWindow, /foreach \(var \(splitter, line\) in _splitterLines\)[\s\S]*?line\.Background = SplitterBrush/);
  assert.match(terminalTab, /_chromePalette = ThemeVisualPalette\.For\(settings\.Theme\);[\s\S]*?ApplyPaneSplitterTheme\(\)/);
  assert.match(terminalTab, /new Microsoft\.UI\.Xaml\.Media\.SolidColorBrush\(_chromePalette\.Divider\)/);
});

test("live theme changes remove WinUI tab insets again after template rebuild", () => {
  assert.match(tabGroup, /private void NormalizeTabStripTemplate\(\)/);
  assert.match(tabGroup, /ActualThemeChanged \+= \(_, _\) => QueueTabTemplateRefresh\(\)/);
  assert.match(tabGroup, /QueueTabTemplateRefresh\(\)/);
  assert.match(tabGroup, /DispatcherQueue\.TryEnqueue\(\(\) => DispatcherQueue\.TryEnqueue/);
});

test("live theme changes recolor the retained right-edge divider brush", () => {
  assert.match(tabGroup, /_tabDividerBrush\.Color = palette\.Divider/);
  assert.match(tabGroup, /Resources\["TabViewBorderBrush"\] = divider/);
  assert.match(tabGroup, /private void ApplyTabStripDivider\(\)[\s\S]*?var divider = _tabDividerBrush/);
});

test("Resesh Dark keeps its original divider and bypasses stale Fluent strokes", () => {
  assert.match(visualPalette, /_ => New\(0x0C0C0C, 0x181818, 0x2B2B2B,/);
  assert.match(tabGroup, /Resources\["TabViewBorderBrush"\] = divider/);
  assert.match(tabGroup, /ActualThemeChanged \+= \(_, _\) => QueueTabTemplateRefresh\(\)/);
});
