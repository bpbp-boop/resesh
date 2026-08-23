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
const filePaneView = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "FilePaneView.cs"),
  "utf8");


test("tabs use one browser-style width and shrink only when the strip is crowded", () => {
  assert.match(xaml, /x:Key="TabViewItemMaxWidth">220</);
  assert.match(xaml, /x:Key="TabViewItemMinWidth">100</);
  assert.match(xaml, /TabWidthMode="Equal"/);
  assert.doesNotMatch(xaml, /TabWidthMode="SizeToContent"/);
});

test("custom tab chrome retains WinUI drag and reorder animations", () => {
  const style = xaml.match(/<Style x:Key="CodeTabStyle"[\s\S]*?<\/Style>/)?.[0] ?? "";

  assert.match(style, /VisualStateGroup x:Name="ReorderHintStates"/);
  assert.equal(style.match(/<DragOverThemeAnimation/g)?.length, 4);
  assert.match(style, /VisualStateGroup x:Name="DragStates"/);
  assert.match(style, /<DragItemThemeAnimation TargetName="LayoutRoot"/);
  assert.match(style, /<FadeOutThemeAnimation TargetName="LayoutRoot"/);
  assert.match(style, /<DropTargetItemThemeAnimation TargetName="LayoutRoot"/);
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

test("each tab bar reserves the measured width of every action button", () => {
  const strip = xaml.match(/x:Name="TabStripHost"[\s\S]*?<\/Grid>\r?\n\r?\n        <Grid x:Name="TerminalHost"/)?.[0] ?? "";

  assert.match(strip, /<ColumnDefinition Width="\*" \/>[\s\S]*?<ColumnDefinition Width="Auto" \/>/);
  assert.match(strip, /x:Name="Tabs"[\s\S]*?Grid\.Column="0"/);
  assert.match(strip, /Grid\.Column="1"[\s\S]*?x:Name="RecordButton"/);
  assert.match(strip, /x:Name="RecordButton"[\s\S]*?Width="30"[\s\S]*?Height="30"/);
  assert.match(strip, /x:Name="RewindButton"[\s\S]*?Width="30"[\s\S]*?Height="30"/);
  assert.match(xaml, /x:Name="RecordStartIcon"[\s\S]*?x:Name="RecordStopIcon"/);
  assert.match(code, /RecordStartIcon\.Visibility = recording \? Visibility\.Collapsed : Visibility\.Visible/);
  assert.match(code, /RecordStopIcon\.Visibility = recording \? Visibility\.Visible : Visibility\.Collapsed/);
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
  assert.match(code, /CurrentFolderButton\.IsEnabled = tab\?\.Capabilities\.FilePane == true[\s\S]*?TabConnectionState\.Connected[\s\S]*?!tab\.IsLocked/);
  assert.match(code, /CurrentFolderButton_Click[\s\S]*?await _host\.OpenFilePaneAtCurrentFolderAsync\(tab\)/);
});

test("local sessions use the direct filesystem pane and Explorer path", () => {
  assert.match(terminalView, /Session\.IsLocal[\s\S]*?new FilePaneView\(\(\) => Session, OpenInExplorerAsync\)/);
  assert.match(terminalView, /OpenInExplorerAsync\(string path\)[\s\S]*?Session\.IsLocal[\s\S]*?OpenLocalDirectoryInExplorer\(path\)[\s\S]*?SshfsIntegration/);
  assert.match(filePaneView, /_localFiles is not null \\|\\| SshfsIntegration\.IsInstalled/);
});

test("the tab-bar toggle follows selection and all file-pane open and close routes", () => {
  assert.match(code, /nameof\(TabGroupViewModel\.SelectedTab\)[\s\S]*?ObserveFilePaneButtonTab\(\)/);
  assert.match(code, /FilePaneOpenChanged \+= ActionButtonView_StateChanged/);
  assert.match(code, /FilePaneToggle\.IsChecked = isOpen/);
  assert.match(terminalView, /public event Action\? FilePaneOpenChanged/);
  assert.equal(terminalView.match(/FilePaneOpenChanged\?\.Invoke\(\)/g)?.length, 2);
});
