const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const source = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "MainWindow.xaml.cs"),
  "utf8");
const terminalViewSource = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Terminal", "TerminalTabView.cs"),
  "utf8");
const appResources = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "App.xaml"),
  "utf8");
const terminalHtml = fs.readFileSync(
  path.join(__dirname, "..", "src", "Terminal", "wwwroot", "terminal.html"),
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

test("pane divider stays one pixel wide while hover color changes", () => {
  const groupActivation = source.match(
    /private void SetSplitterActive[\s\S]*?\n    }\r?\n\r?\n    private Microsoft\.UI\.Xaml\.Media\.Brush SplitterBrush/)?.[0] ?? "";
  const filePaneActivation = terminalViewSource.match(
    /private void SetPaneSplitterActive[\s\S]*?\n    }\r?\n\r?\n    private void ApplyPaneSplitterTheme/)?.[0] ?? "";

  assert.match(groupActivation, /line\.Background = SplitterBrush\(active\)/);
  assert.doesNotMatch(groupActivation, /line\.(?:Width|Height)\s*=/);
  assert.match(filePaneActivation, /ApplyPaneSplitterTheme\(\)/);
  assert.doesNotMatch(filePaneActivation, /_paneSplitterLine\.Width\s*=/);
});

test("dark session divider is lighter than the scrollbar edge", () => {
  const divider = appResources.match(/x:Key="SessionDividerBrush" Color="(#[0-9A-F]{6})"/)?.[1];
  const scrollbarEdge = terminalHtml.match(/const DARK_RULER = \{[\s\S]*?border: "(#[0-9a-f]{6})"/)?.[1];

  assert.ok(divider);
  assert.ok(scrollbarEdge);
  assert.ok(parseInt(divider.slice(1), 16) > parseInt(scrollbarEdge.slice(1), 16));
  assert.match(appResources, /x:Key="Light"[\s\S]*x:Key="SessionDividerBrush" Color="#E6E6E6"/);
});
