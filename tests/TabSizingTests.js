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
const motion = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.Motion.cs"),
  "utf8");
const terminalView = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"),
  "utf8");
const filePaneView = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "FilePaneView.cs"),
  "utf8");
const tabViewModel = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "ViewModels", "TabViewModel.cs"),
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

test("the tab strip owns its motion instead of WinUI's sequenced container transitions", () => {
  // WinUI runs add, delete and reorder one after another and replays them when a group
  // view is re-parented; the strip keeps them off and animates changes itself.
  assert.match(code, /private void NormalizeTabStripTemplate\(\)\s*\{\s*DisableTabContainerTransitions\(\);/);
  assert.match(motion, /list\.ItemContainerTransitions = new Microsoft\.UI\.Xaml\.Media\.Animation\.TransitionCollection\(\);/);
  for (const transition of ["AddDeleteThemeTransition", "ReorderThemeTransition", "EntranceThemeTransition", "ContentThemeTransition"]) {
    assert.doesNotMatch(code, new RegExp(transition));
    assert.doesNotMatch(motion, new RegExp(transition));
  }
});

test("tab changes slide every tab from where it was drawn once layout settles", () => {
  assert.match(code, /Group\.Tabs\.CollectionChanged \+= \(_, e\) =>\s*\{\s*CaptureTabMotion\(\);/);
  // Snapshot what is on screen (layout slot plus any slide still in flight), hold it
  // through intermediate width passes, then move everything at once.
  assert.match(motion, /return LayoutLeft\(container\) \+ InFlightOffset\(container\);/);
  assert.match(motion, /_tabWidthRefreshQueued \|\| _fullTabWidthRefreshQueued\)\s*\{\s*HoldTabsAtOrigins\(\);/);
  assert.match(motion, /SlideElement\(container, offset\)/);
  assert.match(motion, /visual\.StartAnimation\("Translation", slide\)/);
  // New tabs fade in alongside the slide rather than after it.
  assert.match(motion, /SetTranslation\(container, 0\);\s*FadeInTab\(container\);/);
  // The system "Animation effects" setting turns all of it off.
  assert.match(motion, /MotionSettings\.AnimationsEnabled/);
});

test("the divider gap and the active tab move together", () => {
  assert.match(code, /private void UpdateTabStripDivider\(\)\s*\{\s*if \(TabMotionOwnsDivider\(\)\)\s*return;/);
  assert.match(motion, /private void SlideDividerWithActiveTab\(\)[\s\S]*?_motionOrigins\.TryGetValue\(active, out var origin\)/);
  assert.match(motion, /ScaleDivider\(LeftTabStripDivider, fromLeft, toLeft, anchorRight: false\);/);
  assert.match(motion, /ScaleDivider\(RightTabStripDivider, fromRight, toRight, anchorRight: true\);/);
});

test("drag previews slide aside and the drop settles from the preview", () => {
  assert.match(code, /ShiftDragPreview\(i, offset\);/);
  assert.match(code, /if \(_tabDragPreview\.Visibility == Visibility\.Visible\)\s*CaptureTabMotion\(\);/);
  assert.match(motion, /DragPreviewFor\(container\) is \{ \} preview\)\s*return \(\(TranslateTransform\)preview\.RenderTransform\)\.X \+ InFlightOffset\(preview\);/);
  // Inactive headers are transparent; the dragged one needs the strip colour behind it.
  assert.match(code, /_tabDragBackdrop\.Fill = _tabBackgroundBrush;/);
  // The strip's own slide replaces WinUI's fade back from the dragging state.
  assert.match(xaml, /<VisualTransition To="NotDragging" GeneratedDuration="0" \/>/);
});

test("layout rebuilds snap tab motion instead of animating re-parented strips", () => {
  const window = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "MainWindow.TabGroups.cs"),
    "utf8");
  const workspaces = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "MainWindow.Workspaces.cs"),
    "utf8");
  assert.match(window, /foreach \(var groupView in _groupViews\.Values\)\s*\{\s*groupView\.SuppressTabMotion\(\);/);
  assert.match(window, /_groupViews\[sourceGroup\]\.SuppressTabMotion\(\);\s*MoveTabBetweenGroups\(tab, newGroup, 0\);/);
  assert.match(workspaces, /private void ApplyWorkspaceLayout\([^)]*\)\s*\{[\s\S]{0,200}?groupView\.SuppressTabMotion\(\);/);
});

test("tab text trims inside the shared width without moving the close action", () => {
  assert.match(xaml, /<ColumnDefinition Width="\*" \/>/);
  assert.match(xaml, /Grid\.Column="5"[\s\S]*?TextTrimming="CharacterEllipsis"/);
  assert.match(xaml, /Grid\.Column="6"[\s\S]*?Click="TabCloseGlyph_Click"/);
});

test("tabs recalculate their equal width after a close or strip resize", () => {
  assert.match(code, /Group\.Tabs\.CollectionChanged \+= \(_, e\)/);
  assert.match(code, /Tabs\.TabItemsChanged \+= \(_, e\) =>[\s\S]{0,400}?CollectionChange\.ItemRemoved[\s\S]{0,80}?HasShrunkTabs\(\)[\s\S]{0,700}?QueueFullTabWidthRefresh\(\)/);
  // A close under the pointer keeps widths until the pointer leaves the strip, like a browser.
  assert.match(code, /_pointerInTabStrip && e\.Index < Group\.Tabs\.Count\)\s*_tabWidthHoldPending = true;/);
  assert.match(motion, /TabContainerGrid_PointerExited[\s\S]{0,200}?_tabWidthHoldPending = false;\s*CaptureTabMotion\(\);\s*QueueFullTabWidthRefresh\(\);/);
  // Full-width tabs skip the refresh: its SizeToContent pass would flash content widths.
  assert.match(code, /private bool HasShrunkTabs\(\)[\s\S]{0,120}?"TabViewItemMaxWidth"/);
  const resizeHandler = code.slice(
    code.indexOf("Tabs.SizeChanged += (_, _) =>"),
    code.indexOf("TabStripHost.SizeChanged += (_, _) =>"));
  assert.match(resizeHandler, /QueueFullTabWidthRefresh\(\)/);
  assert.match(code, /QueueFullTabWidthRefresh\(\)[\s\S]*?TabWidthMode = TabViewWidthMode\.SizeToContent;[\s\S]{0,100}?TabWidthMode = TabViewWidthMode\.Equal/);
  assert.match(code, /FindDescendant\(Tabs, "TabsItemsPresenter"\)\?\.InvalidateMeasure\(\)/);
  assert.match(code, /Tabs\.InvalidateMeasure\(\)/);
});

test("a newly selected tab waits for container layout before exposing the divider", () => {
  assert.match(code, /Tabs\.LayoutUpdated \+= Tabs_DividerLayoutUpdated/);
  assert.match(code, /Tabs_DividerLayoutUpdated[\s\S]*?UpdateTabStripDivider\(\)/);
  assert.match(code, /activeTab\.ActualWidth > 0[\s\S]*?LeftTabStripDivider\.Width = activeLeft/);
  assert.match(code, /LeftTabStripDivider\.Width = 0;[\s\S]*?RightTabStripDivider\.Width = 0;[\s\S]*?QueueTabDividerRefreshAfterLayout\(\)/);
});

test("each tab bar reserves the measured width of every action button", () => {
  const strip = xaml.match(/x:Name="TabStripHost"[\s\S]*?<\/Grid>\r?\n\r?\n        <Grid x:Name="TerminalHost"/)?.[0] ?? "";

  assert.match(strip, /<ColumnDefinition Width="\*" \/>[\s\S]*?<ColumnDefinition Width="Auto" \/>/);
  assert.match(strip, /x:Name="Tabs"[\s\S]*?Grid\.Column="0"/);
  assert.match(strip, /Grid\.Column="1"[\s\S]*?x:Name="RecordButton"/);
  for (const name of ["RecordButton", "RewindButton", "ShowCommandsButton"]) {
    const button = strip.match(new RegExp(`x:Name="${name}"[\\s\\S]*?<\\/ToggleButton>`))?.[0] ?? "";
    assert.match(button, /Width="32" Height="32"/);
    assert.doesNotMatch(button, /<TextBlock/);
  }
  assert.match(strip, /<ToggleSplitButton x:Name="FilePaneToggle"/);
  assert.match(strip, /Text="Files"/);
  assert.match(strip, /x:Name="CurrentFolderMenuItem"[\s\S]*?Click="CurrentFolderButton_Click"/);
  assert.match(code, /ExpandedTabActions\.ActualWidth \+ 6/);
  assert.match(code, /recording \? "Stop recording" : "Start recording"/);
  assert.match(code, /RecordStartIcon\.Visibility = recording \? Visibility\.Collapsed : Visibility\.Visible/);
  assert.match(code, /RecordStopIcon\.Visibility = recording \? Visibility\.Visible : Visibility\.Collapsed/);
});

test("a hidden tab close action is not focusable and has stable automation metadata", () => {
  const closeButton = xaml.match(/<Button[\s\S]*?Click="TabCloseGlyph_Click"[\s\S]*?<\/Button>/)?.[0] ?? "";

  assert.match(closeButton, /IsEnabled="\{x:Bind CloseInteractive, Mode=OneWay\}"/);
  assert.match(closeButton, /IsTabStop="\{x:Bind CloseInteractive, Mode=OneWay\}"/);
  assert.match(closeButton, /AutomationProperties\.Name="\{x:Bind CloseAutomationName, Mode=OneWay\}"/);
  assert.match(closeButton, /AutomationProperties\.AutomationId="\{x:Bind CloseAutomationId, Mode=OneWay\}"/);
  assert.match(tabViewModel, /CloseAutomationName => \$"Close \{Header\}"/);
  assert.match(tabViewModel, /CloseAutomationId => \$"TabClose_/);
});

test("tab actions collapse into an overflow menu before minimum-width tabs scroll", () => {
  assert.match(xaml, /x:Name="ExpandedTabActions"/);
  assert.match(xaml, /x:Name="TabActionsOverflowButton"[\s\S]*?Visibility="Collapsed"[\s\S]*?<MenuFlyout>/);
  assert.match(xaml, /x:Name="RecordOverflowItem"[\s\S]*?Click="RecordButton_Click"/);
  assert.match(xaml, /x:Name="RewindOverflowItem"[\s\S]*?Click="RewindButton_Click"/);
  assert.match(xaml, /x:Name="ShowCommandsOverflowItem"[\s\S]*?Click="ShowCommandsButton_Click"/);
  assert.match(xaml, /x:Name="CurrentFolderOverflowItem"[\s\S]*?Click="CurrentFolderButton_Click"/);
  assert.match(xaml, /x:Name="FilePaneOverflowItem"[\s\S]*?Click="FilePaneToggle_Click"/);
  assert.match(code, /TabStripHost\.ActualWidth < Group\.Tabs\.Count \* MinimumTabWidth \+ _expandedTabActionsWidth/);
  assert.match(code, /ExpandedTabActions\.Visibility = expandedVisibility;[\s\S]{0,120}?TabActionsOverflowButton\.Visibility/);
  assert.match(code, /FilePaneOverflowItem\.IsChecked = isOpen/);
  assert.match(code, /RecordOverflowItem\.IsEnabled = RecordButton\.IsEnabled/);
});

test("record controls follow the button foreground instead of using warning red", () => {
  const recordButton = xaml.match(/x:Name="RecordButton"[\s\S]*?<\/ToggleButton>/)?.[0] ?? "";
  assert.equal(
    recordButton.match(/Fill="\{Binding Foreground, ElementName=RecordButton\}"/g)?.length,
    2);
  assert.doesNotMatch(recordButton, /#E74856/);
});
test("action buttons dim with their inactive tab group", () => {
  assert.match(code, /TabStripActions\.Opacity = Group\.SelectedTab\?\.IsGroupFocused == false \? 0\.55 : 1\.0/);
  assert.match(code, /nameof\(TabViewModel\.IsGroupFocused\)[\s\S]{0,80}?UpdateTabActionButtons\(\)/);
});

test("the current-folder menu item is connected-only and uses the host action", () => {
  assert.match(code, /CurrentFolderMenuItem\.IsEnabled = tab\?\.Capabilities\.FilePane == true[\s\S]*?TabConnectionState\.Connected[\s\S]*?!tab\.IsLocked/);
  assert.match(code, /CurrentFolderButton_Click[\s\S]*?await _host\.OpenFilePaneAtCurrentFolderAsync\(tab\)/);
});

test("file options use consistent title casing", () => {
  for (const label of [
    "Open at Terminal Folder",
    "Open File Pane at Terminal Folder",
    "Open in File Explorer",
    "Show File Pane",
  ]) {
    assert.match(xaml, new RegExp(`Text="${label}"`));
  }
  assert.match(code, /isOpen \? "Hide File Pane" : "Show File Pane"/);
});

test("local sessions use the direct filesystem pane and Explorer path", () => {
  assert.match(terminalView, /Session\.IsLocal[\s\S]*?new FilePaneView\(\(\) => Session, OpenInExplorerAsync\)/);
  assert.match(terminalView, /OpenInExplorerAsync\(string path\)[\s\S]*?Session\.IsLocal[\s\S]*?OpenLocalDirectoryInExplorer\(path\)[\s\S]*?SshfsIntegration/);
  assert.match(filePaneView, /_localFiles is not null \\|\\| SshfsIntegration\.IsInstalled/);
});

test("SFTP downloads validate each remote name and keep recursive paths below the selected folder", () => {
  assert.match(filePaneView, /WindowsDownloadPath\.ValidateName\(entry\.Name\)/);
  assert.match(filePaneView, /PlanRemoteDirectory\(sftp, entry\.FullPath, localPath, targetDir, files, token\)/);
  assert.match(filePaneView, /WindowsDownloadPath\.Combine\(targetRoot, localDir, child\.Name\)/);
  assert.doesNotMatch(filePaneView, /Path\.Combine\(localDir, child\.Name\)/);
});

test("the tab-bar toggle follows selection and all file-pane open and close routes", () => {
  assert.match(code, /nameof\(TabGroupViewModel\.SelectedTab\)[\s\S]*?ObserveFilePaneButtonTab\(\)/);
  assert.match(code, /FilePaneOpenChanged \+= ActionButtonView_StateChanged/);
  assert.match(code, /FilePaneToggle\.IsChecked = isOpen/);
  assert.match(terminalView, /public event Action\? FilePaneOpenChanged/);
  assert.equal(terminalView.match(/FilePaneOpenChanged\?\.Invoke\(\)/g)?.length, 2);
});
