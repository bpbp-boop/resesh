const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const source = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "TabGroupView.xaml.cs"),
  "utf8");

test("the tab strip observes Enter even when TabView handles the key", () => {
  assert.match(source,
    /Tabs\.AddHandler\(KeyDownEvent, new KeyEventHandler\(Tabs_KeyDown\), true\)/);
});

test("Enter on a selected stopped tab uses the normal reconnect action", () => {
  const handler = source.match(
    /private void Tabs_KeyDown[\s\S]*?\n    }\r?\n\r?\n    private async void Tabs_PointerReleased/)?.[0] ?? "";

  assert.match(handler, /VirtualKey\.Enter/);
  assert.match(handler, /Group\.SelectedTab is \{ \} tab/);
  assert.match(handler, /IsStopped\(tab\)/);
  assert.match(handler, /!IsButtonSource\(e\.OriginalSource\)/);
  assert.match(handler, /_host\.ReconnectTab\(tab\)/);
  assert.match(handler, /e\.Handled = true/);
});
