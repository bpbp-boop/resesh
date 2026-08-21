const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const xaml = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml"),
  "utf8");
const code = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml.cs"),
  "utf8");
const terminalView = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"),
  "utf8");

test("tabs use one browser-style width and shrink only when the strip is crowded", () => {
  assert.match(xaml, /x:Key="TabViewItemMaxWidth">220</);
  assert.match(xaml, /x:Key="TabViewItemMinWidth">100</);
  assert.match(xaml, /TabWidthMode="Equal"/);
  assert.doesNotMatch(xaml, /TabWidthMode="SizeToContent"/);
});

test("tab text trims inside the shared width without moving the close action", () => {
  assert.match(xaml, /<ColumnDefinition Width="\*" \/>/);
  assert.match(xaml, /Grid\.Column="5"[\s\S]*?TextTrimming="CharacterEllipsis"/);
  assert.match(xaml, /Grid\.Column="6"[\s\S]*?Click="TabCloseGlyph_Click"/);
});

test("tabs remeasure after close, move, or newly available group space", () => {
  assert.match(code, /Group\.Tabs\.CollectionChanged \+= \(_, _\) =>[\s\S]{0,120}?QueueTabWidthRefresh\(\)/);
  assert.match(code, /Tabs\.SizeChanged \+= \(_, _\) => QueueTabWidthRefresh\(\)/);
  assert.match(code, /FindDescendant\(Tabs, "TabsItemsPresenter"\)\?\.InvalidateMeasure\(\)/);
  assert.match(code, /Tabs\.InvalidateMeasure\(\)/);
});

test("each tab bar keeps compact current-folder and file-pane buttons at its far right", () => {
  const strip = xaml.match(/x:Name="TabStripHost"[\s\S]*?<\/Grid>\r?\n\r?\n        <Grid x:Name="TerminalHost"/)?.[0] ?? "";

  assert.match(strip, /<ColumnDefinition Width="\*" \/>[\s\S]*?<ColumnDefinition Width="108" \/>/);
  assert.match(strip, /x:Name="Tabs"[\s\S]*?Grid\.Column="0"/);
  assert.match(strip, /Grid\.Column="1"[\s\S]*?x:Name="CurrentFolderButton"/);
  assert.match(strip, /x:Name="CurrentFolderButton"[\s\S]*?Width="30"[\s\S]*?Height="30"[\s\S]*?Padding="0"/);
  assert.doesNotMatch(strip, /x:Name="CurrentFolderButton"[\s\S]*?Background="Transparent"/);
  assert.match(strip, /Click="CurrentFolderButton_Click"/);
  const currentFolder = strip.match(/x:Name="CurrentFolderButton"[\s\S]*?<\/Button>/)?.[0] ?? "";
  assert.match(currentFolder, /<Grid Width="18" Height="18">/);
  assert.match(currentFolder, /Glyph="&#xE8B7;"[\s\S]*?FontSize="16"/);
  assert.match(currentFolder, /<PathIcon[\s\S]*?Data="M0,1\.1/); // ">_" prompt badge
  assert.match(strip, /Grid\.Column="1"[\s\S]*?x:Name="FilePaneToggle"/);
  assert.match(strip, /x:Name="FilePaneToggle"[\s\S]*?Width="30"[\s\S]*?Height="30"[\s\S]*?Padding="0"/);
  assert.doesNotMatch(strip, /x:Name="FilePaneToggle"[\s\S]*?Background="Transparent"/);
  assert.match(strip, /Click="FilePaneToggle_Click"/);
});

test("the current-folder button is connected-only and uses the host action", () => {
  assert.match(code, /CurrentFolderButton\.IsEnabled = tab\?\.Capabilities\.RemoteFiles == true[\s\S]*?TabConnectionState\.Connected[\s\S]*?!tab\.IsLocked/);
  assert.match(code, /CurrentFolderButton_Click[\s\S]*?await _host\.OpenFilePaneAtCurrentFolderAsync\(tab\)/);
});

test("the tab-bar toggle follows selection and all file-pane open and close routes", () => {
  assert.match(code, /nameof\(TabGroupViewModel\.SelectedTab\)[\s\S]*?ObserveFilePaneButtonTab\(\)/);
  assert.match(code, /FilePaneOpenChanged \+= ActionButtonView_StateChanged/);
  assert.match(code, /FilePaneToggle\.IsChecked = isOpen/);
  assert.match(terminalView, /public event Action\? FilePaneOpenChanged/);
  assert.equal(terminalView.match(/FilePaneOpenChanged\?\.Invoke\(\)/g)?.length, 2);
});
