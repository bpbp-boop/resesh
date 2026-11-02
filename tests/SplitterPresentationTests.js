const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const source = require("./support/mainWindowSource");
const terminalViewSource = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"),
  "utf8");
const appResources = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "App.xaml"),
  "utf8");

test("session splitters keep a visible one-pixel divider under a transparent hit target", () => {
  const layoutBuilder = source.match(
    /private FrameworkElement BuildGroupLayoutElement[\s\S]*?\n    }\r?\n\r?\n    public void CloneSession/)?.[0] ?? "";

  assert.match(layoutBuilder, /Background = new Microsoft\.UI\.Xaml\.Media\.SolidColorBrush\(_themePalette\.Divider\)/);
  assert.match(layoutBuilder, /Width = isColumns \? 1 : double\.NaN/);
  assert.match(layoutBuilder, /Height = isColumns \? double\.NaN : 1/);
  assert.doesNotMatch(layoutBuilder, /GridLength\(7\)/);
  assert.equal((layoutBuilder.match(/new GridLength\(1\) \}/g) ?? []).length, 2);
  assert.match(layoutBuilder,
    /Background = new Microsoft\.UI\.Xaml\.Media\.SolidColorBrush\(Microsoft\.UI\.Colors\.Transparent\)/);
  assert.doesNotMatch(layoutBuilder, /Background = .*Resources\["SessionSurfaceBrush"\]/);
});

// The highlighted divider thickens, but only through the helper: a plain Width in the 1px
// track would be clipped back to one pixel.
test("pane divider thickens through SplitterHighlight while the hover color shows", () => {
  const groupActivation = source.match(
    /private void SetSplitterActive[\s\S]*?\n    }\r?\n\r?\n    \/\/\/ <summary>Gutters stay neutral/)?.[0] ?? "";
  const filePaneActivation = terminalViewSource.match(
    /private void SetPaneSplitterActive[\s\S]*?\n    }\r?\n\r?\n    private void UpdateFilePaneWidthLimit/)?.[0] ?? "";

  assert.match(groupActivation, /line\.Background = SplitterBrush\(line, active\)/);
  assert.match(groupActivation, /SplitterHighlight\.SetLineThickness\(\s*line,/);
  assert.doesNotMatch(groupActivation, /line\.(?:Width|Height)\s*=/);
  assert.match(filePaneActivation, /ApplyPaneSplitterTheme\(\)/);
  assert.match(filePaneActivation, /SplitterHighlight\.SetLineThickness\(_paneSplitterLine, columns: true, active\)/);
  assert.doesNotMatch(filePaneActivation, /_paneSplitterLine\.Width\s*=/);

  const helper = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "SplitterHighlight.cs"),
    "utf8");
  assert.match(helper, /var overhang = \(thickness - 1\) \/ 2;/);
  assert.match(helper, /line\.Margin = new Thickness\(-overhang, 0, -overhang, 0\)/);
  assert.match(helper, /line\.Margin = new Thickness\(0, -overhang, 0, -overhang\)/);
  // Above the neighbouring panes, or the panes would cover the thicker line.
  assert.match(source, /Canvas\.SetZIndex\(line, 1\)/);
});

// Hover highlights wait a moment so a mouse passing over a divider doesn't flash it; a press
// lights it at once. Every splitter goes through the same class.
test("splitter highlights wait for a hover delay but light at once on press", () => {
  const helper = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "SplitterHighlight.cs"),
    "utf8");

  assert.match(helper, /HoverDelay = TimeSpan\.FromMilliseconds\(300\)/);
  assert.match(helper, /PointerEntered \+= [\s\S]*?_hoverTimer\.Start\(\)/);
  assert.match(helper, /PointerPressedEvent[\s\S]*?handledEventsToo: true/);
  assert.match(helper, /var active = _hoverShown \|\| _pressed \|\| _dragging;/);
  // The splitter doesn't capture the pointer, so a release off the strip never reaches it:
  // the end of a drag, or leaving without one, has to end the press.
  assert.match(helper, /ManipulationCompleted \+= [\s\S]*?_pressed = false;/);
  assert.match(helper, /private void Leave\(\)[\s\S]*?if \(!_dragging\)\s*_pressed = false;/);
  // The toolkit's own instant hover fill is cleared on each splitter, where its template
  // looks first; app-level overrides don't reach it.
  assert.match(helper, /splitter\.Resources\["SizerBaseBackgroundPointerOver"\] = clear;/);
  assert.match(helper, /splitter\.Resources\["SizerBaseBackgroundPressed"\] = clear;/);

  assert.match(source, /SplitterHighlight\.Attach\(splitter, active => SetSplitterActive\(splitter, active\)\)/);
  assert.match(terminalViewSource, /SplitterHighlight\.Attach\(_paneSplitter, SetPaneSplitterActive\)/);
  for (const text of [source, terminalViewSource])
    assert.doesNotMatch(text, /[sS]plitter\.Pointer(?:Entered|Exited) \+=/);
});

test("session divider brushes are defined per theme", () => {
  assert.match(appResources, /x:Key="SessionDividerBrush" Color="#[0-9A-F]{6}"/);
  assert.match(appResources, /x:Key="Light"[\s\S]*x:Key="SessionDividerBrush" Color="#E6E6E6"/);
});


// A grid clips each child to its cell, hit-testing included. Every splitter sits in a 1px
// divider track, so a plain Width there left a grab area of one pixel. The shared helper's
// negative margins shrink the layout size back to the track while the strip stays wide.
test("splitters size their grab strip through SplitterHitArea so the 1px track cannot clip it", () => {
  const helper = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "SplitterHitArea.cs"),
    "utf8");
  const mainWindowXaml = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "MainWindow.xaml"),
    "utf8");

  const size = Number(helper.match(/public const double Size = (\d+(?:\.\d+)?);/)?.[1]);
  const before = Number(helper.match(/private const double Before = (\d+(?:\.\d+)?);/)?.[1]);
  assert.ok(size >= 8, "grab strip is at least the toolkit's 8 DIP minimum");
  assert.ok(before > 0 && before < size - 1);
  assert.match(helper, /private const double After = Size - 1 - Before;/);
  assert.match(helper, /Margin = new Thickness\(-Before, 0, -After, 0\)/);
  assert.match(helper, /Margin = new Thickness\(0, -Before, 0, -After\)/);

  // Every splitter goes through the helper, and none sets its own size.
  const treeSplitter = mainWindowXaml.match(/<toolkit:GridSplitter[\s\S]*?\/>/)?.[0] ?? "";
  assert.match(treeSplitter, /x:Name="TreeSplitter"/);
  assert.doesNotMatch(treeSplitter, /\b(?:Min)?(?:Width|Height|Margin)=/);
  assert.match(source, /SplitterHitArea\.Apply\(TreeSplitter, columns: true\)/);
  assert.match(source, /SplitterHitArea\.Apply\(splitter, isColumns\)/);
  assert.match(terminalViewSource, /SplitterHitArea\.Apply\(_paneSplitter, columns: true\)/);

  for (const text of [source, terminalViewSource]) {
    for (const creation of text.match(/new CommunityToolkit\.WinUI\.Controls\.GridSplitter\s*\{[\s\S]*?\};/g) ?? [])
      assert.doesNotMatch(creation, /\b(?:Width|Height|Margin|MinWidth|MinHeight)\s*=/);
  }
});

// A layout rebuild drops gutters that may be hovered or pressed. Left in the active set they
// hid the focused-pane frame for the life of the window; unloading puts them back at rest.
test("splitters dropped by a layout rebuild stop counting as active", () => {
  const helper = fs.readFileSync(
    path.join(__dirname, "..", "src", "App", "SplitterHighlight.cs"),
    "utf8");
  const rebuild = source.match(/private void RebuildGroupLayout\(\)[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  const activation = source.match(/private void SetSplitterActive[\s\S]*?\n    }\r?\n/)?.[0] ?? "";

  assert.match(helper, /splitter\.Unloaded \+= \(_, _\) => Reset\(\);/);
  assert.match(helper, /private void Reset\(\)[\s\S]*?_dragging = false;[\s\S]*?Leave\(\);/);
  assert.match(rebuild, /_splitterLines\.Remove\(splitter\);[\s\S]*?_activeSplitters\.Remove\(splitter\);/);
  // Going to rest leaves the set even for a splitter whose line is already gone.
  assert.match(activation, /if \(!_splitterLines\.TryGetValue\(splitter, out var line\)\)[\s\S]*?_activeSplitters\.Remove\(splitter\)/);
});

// The opening animation fades the docked gutter in from a local Opacity of 0. Stopping the
// storyboard alone (on a pane-mode change mid-animation) left the gutter invisible, so every
// stop goes through one helper that restores everything the animation touches.
test("stopping the sessions pane animation restores the pane and the gutter", () => {
  const cancel = source.match(/private void CancelSessionsPaneAnimation\(\)[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(cancel, /_sessionsPaneAnimationVersion\+\+/);
  assert.match(cancel, /_sessionsPaneStoryboard\?\.Stop\(\)/);
  assert.match(cancel, /pane\.Opacity = 1/);
  assert.match(cancel, /transform\.X = 0/);
  assert.match(cancel, /TreeSplitter\.Opacity = 1/);
  assert.match(cancel, /TreeSplitterLine\.Opacity = 1/);

  // Nothing else stops the storyboard.
  assert.equal(source.match(/_sessionsPaneStoryboard\?\.Stop\(\)/g)?.length, 1);
  const paneMode = source.match(/private void UpdatePaneMode\(\)[\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(paneMode, /CancelSessionsPaneAnimation\(\);/);
  const start = source.match(/private void StartSessionsPaneAnimation\([\s\S]*?\n    }\r?\n/)?.[0] ?? "";
  assert.match(start, /^[^]*?\{\s*CancelSessionsPaneAnimation\(\);/);
});
