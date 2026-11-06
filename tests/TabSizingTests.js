const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

// Tab sizing and motion decisions live in TabStripSizing.cs and are tested headless in
// tests/AppLogic.Tests/TabStripSizingTests.cs. These checks cover XAML and wiring only.
const controls = path.join(__dirname, "..", "src", "App", "Controls");
const xaml = fs.readFileSync(path.join(controls, "TabGroupView.xaml"), "utf8");
const code = fs.readFileSync(path.join(controls, "TabGroupView.xaml.cs"), "utf8");
const motion = fs.readFileSync(path.join(controls, "TabGroupView.Motion.cs"), "utf8");
const sizing = fs.readFileSync(path.join(controls, "TabStripSizing.cs"), "utf8");
const terminalView = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"),
  "utf8");
const mainWindow = require("./support/mainWindowSource");

function xamlDouble(key) {
  return Number(xaml.match(new RegExp(`x:Key="${key}">([\\d.]+)<`))?.[1]);
}

function csharpConstant(name) {
  return Number(sizing.match(new RegExp(`const double ${name} = ([\\d.]+);`))?.[1]);
}

test("tabs use one browser-style width and shrink only when the strip is crowded", () => {
  assert.equal(xamlDouble("TabViewItemMaxWidth"), 220);
  assert.equal(xamlDouble("TabViewItemMinWidth"), 100);
  assert.match(xaml, /TabWidthMode="Equal"/);
  assert.doesNotMatch(xaml, /TabWidthMode="SizeToContent"/);
});

test("the sizing logic uses the same tab widths as the XAML", () => {
  assert.equal(csharpConstant("MaximumTabWidth"), xamlDouble("TabViewItemMaxWidth"));
  assert.equal(csharpConstant("MinimumTabWidth"), xamlDouble("TabViewItemMinWidth"));
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

test("the tab strip owns its motion instead of WinUI's sequenced container transitions", () => {
  // WinUI runs add, delete and reorder one after another and replays them when a group
  // view is re-parented; the strip keeps them off and animates changes itself.
  assert.match(code, /private void NormalizeTabStripTemplate\(\)\s*\{[\s\S]*?DisableTabContainerTransitions\(\);/);
  assert.match(motion, /ItemContainerTransitions = new Microsoft\.UI\.Xaml\.Media\.Animation\.TransitionCollection\(\);/);
  for (const transition of ["AddDeleteThemeTransition", "ReorderThemeTransition", "EntranceThemeTransition", "ContentThemeTransition"]) {
    assert.doesNotMatch(code, new RegExp(transition));
    assert.doesNotMatch(motion, new RegExp(transition));
  }
});

test("tab changes snapshot where tabs are drawn before the collection moves them", () => {
  assert.match(code, /Group\.Tabs\.CollectionChanged \+= \(_, e\) =>\s*\{\s*CaptureTabMotion\(\);/);
});

test("a drop settles from the drag preview over the strip colour", () => {
  assert.match(code, /if \(_tabDragPreview\.Visibility == Visibility\.Visible\)\s*CaptureTabMotion\(\);/);
  // Inactive headers are transparent; the dragged one needs the strip colour behind it.
  assert.match(code, /_tabDragBackdrop\.Fill = _tabBackgroundBrush;/);
  // The strip's own slide replaces WinUI's fade back from the dragging state.
  assert.match(xaml, /<VisualTransition To="NotDragging" GeneratedDuration="0" \/>/);
});

test("layout rebuilds snap tab motion instead of animating re-parented strips", () => {
  assert.match(mainWindow, /foreach \(var groupView in _groupViews\.Values\)\s*\{\s*groupView\.SuppressTabMotion\(\);/);
  assert.match(mainWindow, /_groupViews\[sourceGroup\]\.SuppressTabMotion\(\);\s*(?:\/\/.*\s*)*MoveTabBetweenGroups\(tab, newGroup, 0, userInitiated: true\);/);
  assert.match(mainWindow, /private void ApplyWorkspaceLayout\([^)]*\)\s*\{[\s\S]{0,200}?groupView\.SuppressTabMotion\(\);/);
});

test("tab text trims inside the shared width without moving the close action", () => {
  const header = xaml.match(/<TabViewItem\.Header>[\s\S]*?<\/TabViewItem\.Header>/)?.[0] ?? "";
  const columns = [...(header.match(/<Grid\.ColumnDefinitions>([\s\S]*?)<\/Grid\.ColumnDefinitions>/)?.[1] ?? "")
    .matchAll(/<ColumnDefinition Width="([^"]+)" \/>/g)].map(match => match[1]);
  const star = columns.indexOf("*");
  assert.ok(star >= 0, "a star column takes the remaining width");
  const column = pattern => Number(header.match(pattern)?.[1]);
  // The title fills the star column and trims; the close button sits in a later fixed one.
  assert.equal(column(/Grid\.Column="(\d+)"(?:(?!Grid\.Column=)[\s\S])*?x:Name="TabTitleText"/), star);
  assert.match(header, /x:Name="TabTitleText"[\s\S]*?TextTrimming="CharacterEllipsis"/);
  assert.ok(column(/Grid\.Column="(\d+)"(?:(?!Grid\.Column=)[\s\S])*?Click="TabCloseGlyph_Click"/) > star);
});

test("tabs recalculate their equal width after a close or strip resize", () => {
  assert.match(code, /Tabs\.TabItemsChanged \+= \(_, e\) =>/);
  const resizeHandler = code.slice(
    code.indexOf("Tabs.SizeChanged += (_, _) =>"),
    code.indexOf("TabStripHost.SizeChanged += (_, _) =>"));
  assert.match(resizeHandler, /QueueFullTabWidthRefresh\(\)/);
  // WinUI caches the equal width; flipping the mode is what makes it recalculate.
  assert.match(code, /TabWidthMode = TabViewWidthMode\.SizeToContent;\s*Tabs\.TabWidthMode = TabViewWidthMode\.Equal;/);
  assert.match(code, /FindDescendant\(Tabs, "TabsItemsPresenter"\)\?\.InvalidateMeasure\(\)/);
  // WinUI releases its own held widths when the pointer leaves this template part.
  assert.match(motion, /FindDescendant\(Tabs, "TabContainerGrid"\)/);
  assert.match(motion, /grid\.PointerExited \+= TabContainerGrid_PointerExited;/);
});

test("the divider refreshes after layout once a new tab has bounds", () => {
  assert.match(code, /Tabs\.LayoutUpdated \+= Tabs_DividerLayoutUpdated/);
  assert.match(code, /Tabs_DividerLayoutUpdated[\s\S]*?UpdateTabStripDivider\(\)/);
});

test("each tab bar reserves the measured width of every action button", () => {
  const strip = xaml.slice(xaml.indexOf('x:Name="TabStripHost"'), xaml.indexOf('x:Name="TerminalHost"'));

  assert.match(strip, /<ColumnDefinition Width="\*" \/>[\s\S]*?<ColumnDefinition Width="Auto" \/>/);
  assert.match(strip, /x:Name="Tabs"[\s\S]*?Grid\.Column="0"/);
  assert.match(strip, /Grid\.Column="1"[\s\S]*?x:Name="RecordButton"/);
  for (const name of ["RecordButton", "RewindButton", "ShowCommandsButton"]) {
    const button = strip.match(new RegExp(`x:Name="${name}"[\\s\\S]*?<\\/ToggleButton>`))?.[0] ?? "";
    assert.match(button, /Width="32" Height="32"/);
    assert.doesNotMatch(button, /<TextBlock/);
  }
  const filesToggle = strip.match(/<ToggleButton x:Name="FilePaneToggle"[\s\S]*?<\/ToggleButton>/)?.[0] ?? "";
  assert.match(filesToggle, /Text="Files"/);
  assert.doesNotMatch(filesToggle, /Flyout/);
  assert.doesNotMatch(strip, /ToggleSplitButton/);
  assert.match(strip, /x:Name="FilePaneDivider"[\s\S]*?x:Name="FilePaneToggle"/);
  assert.match(code, /ExpandedTabActions\.SizeChanged \+= \(_, _\) => UpdateTabActionLayout\(\);/);
  assert.match(code, /RecordStartIcon\.Visibility = recording \? Visibility\.Collapsed : Visibility\.Visible/);
  assert.match(code, /RecordStopIcon\.Visibility = recording \? Visibility\.Visible : Visibility\.Collapsed/);
});

test("a hidden tab close action is not focusable and has stable automation metadata", () => {
  const closeButton = xaml.match(/<Button[\s\S]*?Click="TabCloseGlyph_Click"[\s\S]*?<\/Button>/)?.[0] ?? "";

  assert.match(closeButton, /IsEnabled="\{x:Bind CloseInteractive, Mode=OneWay\}"/);
  assert.match(closeButton, /IsTabStop="\{x:Bind CloseInteractive, Mode=OneWay\}"/);
  assert.match(closeButton, /AutomationProperties\.Name="\{x:Bind CloseAutomationName, Mode=OneWay\}"/);
  assert.match(closeButton, /AutomationProperties\.AutomationId="\{x:Bind CloseAutomationId, Mode=OneWay\}"/);
});

test("tab actions collapse into an overflow menu before minimum-width tabs scroll", () => {
  assert.match(xaml, /x:Name="ExpandedTabActions"/);
  assert.match(xaml, /x:Name="TabActionsOverflowButton"[\s\S]*?Visibility="Collapsed"[\s\S]*?<MenuFlyout>/);
  assert.match(xaml, /x:Name="RecordOverflowItem"[\s\S]*?Click="RecordButton_Click"/);
  assert.match(xaml, /x:Name="RewindOverflowItem"[\s\S]*?Click="RewindButton_Click"/);
  assert.match(xaml, /x:Name="ShowCommandsOverflowItem"[\s\S]*?Click="ShowCommandsButton_Click"/);
  assert.match(xaml, /x:Name="CurrentFolderOverflowItem"[\s\S]*?Click="CurrentFolderButton_Click"/);
  assert.match(xaml, /x:Name="FilePaneOverflowItem"[\s\S]*?Click="FilePaneToggle_Click"/);
  assert.match(code, /ExpandedTabActions\.Visibility = expandedVisibility;[\s\S]{0,120}?TabActionsOverflowButton\.Visibility/);
  assert.match(code, /FilePaneOverflowItem\.IsChecked = isOpen/);
  assert.match(code, /RecordOverflowItem\.IsEnabled = RecordButton\.IsEnabled/);
});

test("record controls follow the button foreground instead of using warning red", () => {
  const recordButton = xaml.match(/x:Name="RecordButton"[\s\S]*?<\/ToggleButton>/)?.[0] ?? "";
  // An outlined ring at rest keeps the idle button as quiet as its neighbours.
  assert.match(recordButton, /x:Name="RecordStartIcon"[^>]*Stroke="\{x:Bind RecordButton\.Foreground, Mode=OneWay\}"/);
  assert.match(recordButton, /x:Name="RecordStopIcon"[\s\S]*?Fill="\{x:Bind RecordButton\.Foreground, Mode=OneWay\}"/);
  assert.doesNotMatch(recordButton, /#E74856/);
});

test("action buttons refresh when their group gains or loses focus", () => {
  assert.match(code, /nameof\(TabViewModel\.IsGroupFocused\)[\s\S]{0,80}?UpdateTabActionButtons\(\)/);
});

test("the current-folder action uses the host action", () => {
  assert.match(code, /CurrentFolderButton_Click[\s\S]*?await _host\.OpenFilePaneAtCurrentFolderAsync\(tab\)/);
});

test("the tab-bar toggle follows selection and all file-pane open and close routes", () => {
  assert.match(code, /nameof\(TabGroupViewModel\.SelectedTab\)[\s\S]*?ObserveFilePaneButtonTab\(\)/);
  assert.match(code, /FilePaneOpenChanged \+= ActionButtonView_StateChanged/);
  assert.match(code, /FilePaneToggle\.IsChecked = isOpen/);
  assert.match(terminalView, /public event Action\? FilePaneOpenChanged/);
  assert.equal(terminalView.match(/FilePaneOpenChanged\?\.Invoke\(\)/g)?.length, 2);
});
