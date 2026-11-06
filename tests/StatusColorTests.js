const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const read = (...parts) => fs.readFileSync(path.join(__dirname, "..", ...parts), "utf8");
const appXaml = read("src", "App", "App.xaml");
const appCode = read("src", "App", "App.xaml.cs");
const presentation = read("src", "App", "PresentationValues.cs");
const visualPalette = read("src", "App", "ThemeVisualPalette.cs");
const mainWindow = require("./support/mainWindowSource");

const statusBrushes = ["Info", "Success", "Caution", "Critical", "Neutral"].map(name => `SessionStatus${name}Brush`);

function themeDictionary(key) {
  const body = appXaml.match(new RegExp(`<ResourceDictionary x:Key="${key}">([\\s\\S]*?)</ResourceDictionary>`))?.[1];
  assert.ok(body, `${key} theme dictionary`);
  return body;
}

test("status colors are theme brushes with High Contrast variants in system colors", () => {
  for (const key of [...statusBrushes, "SessionBadgeGlyphBrush", "SessionRecordingBrush"]) {
    for (const theme of ["Dark", "Light"])
      assert.match(themeDictionary(theme), new RegExp(`x:Key="${key}"`), `${key} in ${theme}`);
    assert.match(
      themeDictionary("HighContrast"),
      new RegExp(`x:Key="${key}" Color="\\{ThemeResource SystemColor\\w+Color\\}"`),
      `${key} in HighContrast`);
  }
});

test("each status hue is typed once, in App.xaml", () => {
  // Colour tags in the session editors are the user's choices, not status marks.
  for (const hex of ["0078D4", "16C60C", "FFB900", "E74856"]) {
    assert.equal(appXaml.match(new RegExp(`#${hex}`, "g"))?.length, 1, `#${hex} in App.xaml`);
    for (const [name, source] of [["PresentationValues", presentation], ["ThemeVisualPalette", visualPalette]])
      assert.doesNotMatch(source, new RegExp(`0x${hex}`, "i"), `${name} repeats ${hex}`);
  }
});

test("tab dots and agent badges look their colors up by key", () => {
  for (const method of ["StateColor", "AgentBadgeColor"]) {
    const body = presentation.match(new RegExp(`public static Windows\\.UI\\.Color ${method}\\([\\s\\S]*?;\\r?\\n\\r?\\n`))?.[0] ?? "";
    assert.match(body, /StatusColor\(/, method);
    assert.doesNotMatch(body, /Rgb\(/, method);
  }
  for (const key of statusBrushes)
    assert.match(presentation, new RegExp(`"${key}"`), key);
});

test("the app's palette brushes take their colors from the one palette in code", () => {
  // Declared without colors, so App.xaml can't hold a second copy of the dark palette.
  const declared = [...appXaml.matchAll(/<SolidColorBrush x:Key="(\w+)" \/>/g)].map(match => match[1]);
  const applied = [...visualPalette.matchAll(/Set\("(\w+)", \w+\);/g)].map(match => match[1]);
  assert.ok(declared.length >= 10);
  assert.deepEqual([...declared].sort(), [...new Set(applied)].sort());
  // At startup, before any window draws, and on every theme change after.
  assert.match(appCode, /void OnLaunched\([\s\S]*?ThemeVisualPalette\.For\(ResolveTheme\(Settings\.Current\.Theme\)\)\.ApplyToAppResources\(\);[\s\S]*?CreateWindowCore\(/);
  assert.match(mainWindow, /private void ApplyThemePalette\(string theme\)[\s\S]*?palette\.ApplyToAppResources\(\);/);
});

test("code-built UI resolves Fluent theme brushes against its own theme", () => {
  // The app's RequestedTheme is the startup theme; a lookup through Application.Current
  // finds that theme's brush after the user switches. ThemeResources resolves against the
  // element's ActualTheme and follows its changes.
  const fluentLookup = /Application\.Current\.Resources\s*(?:\[|\.TryGetValue\()[^\]]*?"(?:TextFillColor|SystemFillColor|DividerStrokeColor|LayerFillColor)\w*Brush"/;
  for (const parts of [
    ["Controls", "FilePaneView.cs"],
    ["Controls", "SshAlgorithmPicker.cs"],
    ["Controls", "HighlightRulesEditor.xaml.cs"],
    ["Dialogs", "TmuxSessionRowView.xaml.cs"],
    ["Dialogs", "TunnelForm.xaml.cs"],
  ]) {
    const source = read("src", "App", ...parts);
    assert.doesNotMatch(source, fluentLookup, parts.join("/"));
  }
  assert.match(read("src", "App", "Controls", "FilePaneView.cs"), /ThemeResources\.SetBrush\(_statusText,/);
  assert.match(read("src", "App", "Dialogs", "TmuxSessionRowView.xaml"),
    /BorderBrush="\{ThemeResource SystemFillColorCautionBrush\}"/);
});

test("code never reads App.xaml's themed brushes through the application", () => {
  // Their High Contrast entries differ, and the application answers for its startup theme.
  const themed = new Set(["Dark", "Light", "HighContrast"].flatMap(theme =>
    [...themeDictionary(theme).matchAll(/x:Key="(\w+)"/g)].map(match => match[1])));
  assert.ok(themed.has("SessionSplitterHoverBrush"));
  const sources = fs.readdirSync(path.join(__dirname, "..", "src", "App"), { recursive: true })
    .filter(file => file.endsWith(".cs") && path.basename(file) !== "ThemeResources.cs");
  for (const file of sources) {
    const source = read("src", "App", file);
    for (const match of source.matchAll(/Application\.Current\.Resources\s*(?:\[|\.TryGetValue\()\s*"(\w+)"/g))
      assert.ok(!themed.has(match[1]), `${file} reads ${match[1]} through Application.Current`);
  }
});
