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

test("tabs use one browser-style width and shrink only when the strip is crowded", () => {
  assert.match(xaml, /x:Key="TabViewItemMaxWidth">240</);
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
  assert.match(code, /Group\.Tabs\.CollectionChanged \+= \(_, _\) => QueueTabWidthRefresh\(\)/);
  assert.match(code, /Tabs\.SizeChanged \+= \(_, _\) => QueueTabWidthRefresh\(\)/);
  assert.match(code, /FindDescendant\(Tabs, "TabsItemsPresenter"\)\?\.InvalidateMeasure\(\)/);
  assert.match(code, /Tabs\.InvalidateMeasure\(\)/);
});
