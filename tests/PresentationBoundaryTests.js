const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const read = (...parts) => fs.readFileSync(path.join(__dirname, "..", ...parts), "utf8");
const tabViewModel = read("src", "App", "ViewModels", "TabViewModel.cs");
const treeViewModel = read("src", "App", "ViewModels", "TreeNodeViewModel.cs");
const presentation = read("src", "App", "PresentationValues.cs");
const tabXaml = read("src", "App", "Controls", "TabGroupView.xaml");
const mainXaml = read("src", "App", "MainWindow.xaml");
const mainCode = read("src", "App", "MainWindow.xaml.cs");

test("tab and tree view models expose data instead of WinUI presentation objects", () => {
  for (const [name, source] of [["tab", tabViewModel], ["tree", treeViewModel]]) {
    assert.doesNotMatch(source, /Microsoft\.UI|Windows\.UI|\bVisibility\b|\bImageSource\b|\bBrush\b/,
      `${name} view model still exposes a WinUI presentation type`);
    assert.doesNotMatch(source, /Application\.Current\.Resources|App\.Icons|ThemeVisualPalette/,
      `${name} view model still resolves view resources`);
  }

  assert.match(tabViewModel, /public string AppTheme => _appTheme/);
  assert.match(tabViewModel, /public string AgentIconKey/);
  assert.match(tabViewModel, /public string\? ColorTag => Session\.ColorTag/);
  assert.match(treeViewModel, /public string\? IconKey => Session\?\.Icon/);
  assert.match(treeViewModel, /public string\? ColorTag => Session\?\.ColorTag/);
});

test("agent icon binding uses a non-null sentinel so x:Bind clears stale images", () => {
  const property = tabViewModel.match(/public string AgentIconKey[\s\S]*?;/)?.[0] ?? "";
  assert.match(property, /AgentIdentities\.None/);
  assert.doesNotMatch(property, /\?\s*AgentIconKey|null/);
});

test("the view layer owns colors, brushes, images, font weights, and visibility", () => {
  assert.match(presentation, /public static class PresentationValues/);
  assert.match(presentation, /TabHeaderBackgroundColor/);
  assert.match(presentation, /TabHeaderForegroundColor/);
  assert.match(presentation, /StateColor/);
  assert.match(presentation, /AgentIcon/);
  assert.match(presentation, /TreeSelectionBackground/);
  assert.match(tabXaml, /local:PresentationValues\.TabHeaderBackgroundColor/);
  assert.match(mainXaml, /local:PresentationValues\.TreeSelectionBackground/);
});

test("workspace, recent-session, and recording templates use typed x:Bind", () => {
  const workspace = mainXaml.match(/x:Name="WorkspaceList"[\s\S]*?<\/ListView>/)?.[0] ?? "";
  const recent = mainXaml.match(/x:Name="RecentSessionList"[\s\S]*?<\/ListView>/)?.[0] ?? "";
  const recordings = mainXaml.match(/x:Name="RecordingList"[\s\S]*?<\/ListView>/)?.[0] ?? "";

  assert.match(workspace, /DataTemplate x:DataType="vm:WorkspaceItemViewModel"/);
  assert.match(recent, /DataTemplate x:DataType="vm:TreeNodeViewModel"/);
  assert.match(recordings, /DataTemplate x:DataType="vm:RecordingItemViewModel"/);
  for (const template of [workspace, recent, recordings])
    assert.doesNotMatch(template, /\{Binding/);
});

test("observable collection bindings state OneWay mode explicitly", () => {
  assert.match(tabXaml, /TabItemsSource="\{x:Bind Group\.Tabs, Mode=OneWay\}"/);
  assert.match(mainXaml, /ItemsSource="\{x:Bind ViewModel\.RootNodes, Mode=OneWay\}"/);
  assert.match(mainXaml, /ItemsSource="\{x:Bind Workspaces, Mode=OneWay\}"/);
  assert.match(mainXaml, /ItemsSource="\{x:Bind RecentSessions, Mode=OneWay\}"/);
  assert.match(mainXaml, /ItemsSource="\{x:Bind Recordings, Mode=OneWay\}"/);
});

test("tree selection brushes live at app scope and update with the theme palette", () => {
  const appXaml = read("src", "App", "App.xaml");
  assert.match(appXaml, /x:Key="SessionTreeSelectionBrush"/);
  assert.match(appXaml, /x:Key="SessionTreeSelectionForegroundBrush"/);
  assert.match(mainCode, /SessionTreeSelectionBrush"\]\)\.Color = palette\.TreeSelection/);
  assert.match(mainCode, /SessionTreeSelectionForegroundBrush"\]\)\.Color = palette\.TreeSelectionForeground/);
  assert.doesNotMatch(mainCode, /TreeNodeViewModel\.ApplySelectionTheme/);
});
