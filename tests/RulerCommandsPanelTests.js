const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const surfaceSource = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "Native", "NativeTerminalSurface.cs"),
  "utf8");

test("the native Show commands button reaches the terminal panel through the tab view", () => {
  const tabView = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"), "utf8");
  const groupXaml = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml"), "utf8");
  const groupCode = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml.cs"), "utf8");

  assert.match(surfaceSource, /public override void ToggleCommandsPanel\(\) => SetCommandsPanelOpen\(!_commandsPanelOpen\);/);
  assert.match(tabView, /public void ToggleCommandsPanel\(\)[\s\S]*?!_tab\.IsLocked[\s\S]*?_terminal\.ToggleCommandsPanel\(\)/);
  assert.match(groupXaml, /<ToggleButton[\s\S]{0,400}?x:Name="ShowCommandsButton"[\s\S]*?Click="ShowCommandsButton_Click"/);
  assert.match(groupXaml, /x:Name="ShowCommandsButton"[\s\S]*?Glyph="&#xE756;"/);
  assert.match(groupCode, /ShowCommandsButton_Click[\s\S]*?ToggleCommandsPanel\(\)/);
  // The whole action cluster hides when the group has no tabs.
  assert.match(groupCode, /TabStripActions\.Visibility = Group\.Tabs\.Count > 0/);
  assert.match(groupCode, /ShowCommandsButton\.IsEnabled = tab is not null && !tab\.IsLocked/);
});

test("an active toggle keeps flat chrome and shows an accent icon", () => {
  const tabView = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"), "utf8");
  const groupXaml = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml"), "utf8");
  const groupCode = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml.cs"), "utf8");

  // Panel state -> surface event -> tab view property -> IsChecked on the button.
  assert.match(surfaceSource, /_commandsPanelOpen = open;[\s\S]*?CommandsPanelOpenChanged\?\.Invoke\(open\);/);
  assert.match(tabView, /_terminal\.CommandsPanelOpenChanged \+= open =>[\s\S]*?IsCommandsPanelOpen = open/);
  assert.match(groupCode, /CommandsPanelOpenChanged \+= ActionButtonView_StateChanged/);
  assert.match(groupCode, /ShowCommandsButton\.IsChecked = commandsOpen/);

  // Resting buttons and checked toggles are flat. Hover and press reveal the
  // theme-specific surface, while checked state recolors the glyph.
  const actions = groupXaml.match(/x:Name="TabStripActions"[\s\S]*?<\/Grid>\r?\n\r?\n        <Grid x:Name="TerminalHost"/)?.[0]
    ?? groupXaml.match(/x:Name="TabStripActions"[\s\S]*$/)?.[0] ?? "";
  assert.match(actions, /x:Key="ToggleButtonForegroundChecked" ResourceKey="AccentTextFillColorPrimaryBrush"/);
  assert.match(actions, /x:Key="ToggleButtonBackgroundChecked" ResourceKey="TabActionButtonRestBrush"/);
  assert.match(actions, /x:Key="ToggleButtonBackgroundCheckedPointerOver" ResourceKey="TabActionButtonHoverBrush"/);
  assert.match(actions, /<Style TargetType="Button"[\s\S]*?BorderThickness" Value="0"/);
  assert.match(actions, /<Style TargetType="ToggleButton"[\s\S]*?BorderThickness" Value="0"/);
  assert.match(actions, /x:Key="ButtonForegroundPressed" ResourceKey="AccentTextFillColorPrimaryBrush"/);
  // Dark, Light, and High Contrast dictionaries carry the aliases so a runtime
  // theme swap re-resolves them.
  assert.equal((actions.match(/x:Key="ToggleButtonForegroundChecked"/g) || []).length, 3);
  assert.match(actions, /x:Key="HighContrast"[\s\S]*?SystemColorHighlightTextColor/);
});
