"use strict";

const assert = require("assert");
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const test = require("node:test");

const root = path.join(__dirname, "..");
const api = fs.readFileSync(path.join(root, "src", "Terminal", "NativeTerminalApi.cs"), "utf8");
const surface = fs.readFileSync(path.join(root, "src", "Terminal", "NativeTerminalSurface.cs"), "utf8");
const ruler = fs.readFileSync(path.join(root, "src", "Terminal", "NativeTerminalRuler.cs"), "utf8");
const panel = fs.readFileSync(path.join(root, "src", "Terminal", "NativeTerminalCommandsPanel.cs"), "utf8");
const buildScript = fs.readFileSync(path.join(root, "eng", "build-native-terminal.ps1"), "utf8");
const inputPatch = fs.readFileSync(
  path.join(root, "eng", "native-terminal-patches", "input-handled.patch"),
  "utf8"
);
const capabilities = JSON.parse(fs.readFileSync(
  path.join(root, "eng", "native-terminal-capabilities.json"),
  "utf8"
));

test("ABI 2.0 owns composition hosting and stable annotation data", () => {
  assert.match(api, /AbiMajor\s*=\s*2/);
  for (const name of [
    "ReseshTerminalSetBounds",
    "ReseshTerminalAttachSwapChainPanel",
    "ReseshTerminalSendPointerEvent",
    "ReseshTerminalGetMarks",
    "ReseshTerminalGetSearchRows",
    "ReseshTerminalGetMarkText",
    "ReseshTerminalScrollToMark",
    "ReseshTerminalGetCursorLogicalLine",
    "ReseshTerminalCreateApplicationMark",
    "ReseshTerminalDiscardPromptProbe",
    "ReseshTerminalAddBookmark",
    "ReseshTerminalRemoveBookmark",
    "ReseshTerminalClearBookmarks",
  ]) {
    assert.match(api, new RegExp(name), `${name} must be a required ABI 2.0 export`);
  }
  assert.match(api, /record struct MarkRecord\([\s\S]*?ulong Id,[\s\S]*?ulong Generation/);
  assert.match(api, /JsonDocument\.Parse\(File\.ReadAllText\(manifestPath\)\)/);
  assert.match(api, /internal bool SendKeyEvent\([\s\S]*?out var handled/);
  assert.match(api, /byte keyDown,[\s\S]*?out byte handled/);
  assert.match(surface, /PreviewKeyDown \+= OnTerminalKeyDown/);
  assert.match(surface, /args\.Handled = _api\.SendKeyEvent/);
  assert.match(inputPatch, /uint8_t\* handled/);
  assert.match(inputPatch, /bool HwndTerminal::_SendKeyEvent/);
});

test("the composition patch pin is stable across Git line endings", () => {
  const manifest = JSON.parse(fs.readFileSync(
    path.join(root, "eng", "native-terminal.json"),
    "utf8"
  ).replace(/^\uFEFF/, ""));
  for (const patch of manifest.fork.patches) {
    const patchText = fs.readFileSync(
      path.join(root, "eng", patch.file),
      "utf8"
    ).replace(/\r\n?/g, "\n");
    const actual = crypto.createHash("sha256").update(patchText, "utf8").digest("hex");
    assert.strictEqual(patch.sha256, actual, `${patch.file} must match its normalized hash`);
  }
  assert.match(buildScript, /normalizedPatchText[\s\S]*?Replace\("`r`n", "`n"\)/);
  assert.match(buildScript, /git -C \$ForkPath apply --ignore-space-change --check/);
  assert.match(buildScript, /\$ForkPath = Get-AbsolutePath \$ForkPath/);
  assert.match(buildScript, /requiredComponents = @\("Microsoft\.VisualStudio\.Component\.VC\.Tools\.x86\.x64"\)/);
  assert.match(buildScript, /\$Architecture -in @\("ARM64", "All"\)[\s\S]*?Microsoft\.VisualStudio\.Component\.VC\.Tools\.ARM64/);
  assert.match(buildScript, /\$Architecture -in @\("ARM64", "All"\)[\s\S]*?Microsoft\.VisualStudio\.Component\.UWP\.VC\.ARM64/);
  assert.match(buildScript, /\/nr:false/);
  assert.match(buildScript, /PlatformToolset=\$\(\$manifest\.toolchain\.platformToolset\)/);
  assert.match(buildScript, /TargetPlatformVersion=\$\(\$manifest\.toolchain\.windowsSdk\)/);
  assert.match(buildScript, /AppxBundlePlatforms=\$item/);
});

test("Enter-gated discovery waits for echo and yields to exact shell marks", () => {
  const keyDown = surface.slice(
    surface.indexOf("private void OnTerminalKeyDown"),
    surface.indexOf("private void OnTerminalKeyUp")
  );
  assert.ok(keyDown.indexOf("BeginPromptDiscovery()") < keyDown.indexOf("_api.SendKeyEvent"));
  assert.match(surface, /Task\.Delay\(attempt == 0 \? 300 : 900/);
  assert.match(surface, /epoch != _titleEpoch/);
  assert.match(surface, /_exactShellMarksSeen[\s\S]*?CancelPromptProbes\(\)/);
  assert.match(surface, /ObserveOsc3008[\s\S]*?_api\.CreateApplicationMark/);
});

test("the native ruler delegates input and completes matched native scrolls", () => {
  assert.match(ruler, /ScrollBar _scrollBar = new\(\)/);
  assert.match(ruler, /_pendingScrolls\.Any\(scroll => scroll\.Target == _viewTop\)/);
  assert.match(ruler, /_scrollBar\.Scroll \+= OnScrollBarScroll/);
  assert.match(ruler, /_scrollBar\.IndicatorMode = ScrollingIndicatorMode\.MouseIndicator/);
  assert.match(ruler, /_scrollBar\.Maximum = maximum/);
  assert.match(ruler, /_scrollBar\.ViewportSize = _viewportHeight/);
  assert.match(ruler, /_scrollBar\.IsEnabled = maximum > 0 && !alternateBuffer/);
  assert.match(ruler, /Canvas _annotations[\s\S]*?IsHitTestVisible = false/);
  assert.match(ruler, /HashSet<\(int Lane, int Bucket, uint Color\)>/);
  assert.match(ruler, /Visibility = alternateBuffer \? Visibility\.Collapsed/);
});

test("the native ruler uses a composition-safe TeachingTip for mark actions", () => {
  assert.match(ruler, /TeachingTip _markTip = new\(\)/);
  assert.match(ruler, /TeachingTipPlacementMode\.Left/);
  assert.match(ruler, /IsLightDismissEnabled = true/);
  assert.match(ruler, /AutomationId\(_annotationInput, "NativeTerminalAnnotations"\)/);
  assert.match(ruler, /PointerMovedEvent[\s\S]*?ShowMarkPreview\(nearest\)/);
  assert.match(ruler, /_markTip\.IsOpen = true/);
  assert.match(ruler, /CopyRequested\?\.Invoke\(_activeMarkId\)/);
  assert.doesNotMatch(ruler, /NativeTerminalMarkPopup|AppWindow|SetWindowLongPtr/);
  assert.match(surface, /SwapChainPanel _terminalPanel = new\(\)/);
  assert.doesNotMatch(surface, /_terminalPanel\.Background\s*=/);
  assert.match(surface, /AttachSwapChainPanel/);
  assert.match(surface, /_ruler\.CopyRequested \+= CopyMarkOutput/);
  assert.match(surface, /return CopyToClipboard\(text, null, null\)/);
});

test("the commands pane is docked, virtualized, and keeps actions outside the ruler", () => {
  assert.match(panel, /PreferredWidth = 400/);
  assert.match(panel, /ListView _list = new\(\)/);
  assert.match(panel, /ItemsSource = commands/);
  assert.match(panel, /ItemTemplate = CommandTemplate/);
  assert.match(panel, /DataTemplate[\s\S]*?\{Binding Text\}/);
  assert.match(panel, /public string Text => _text \?\?= ReadText\(\)/);
  assert.match(panel, /JumpRequested/);
  assert.match(panel, /CopyRequested/);
  assert.match(surface, /_terminalPanel\.Margin = new Thickness\(0, findHeight, chromeWidth, 0\)/);
  assert.match(surface, /_commandsPanel\.JumpRequested \+= ScrollToMark/);
  assert.match(surface, /_commandsPanel\.CopyRequested \+=[^\n]*CopyMarkOutput/);
});

test("Phase 5 capability rows are complete", () => {
  for (const name of ["command marks", "overview ruler", "bookmarks", "commands panel"]) {
    const capability = capabilities.capabilities.find((item) => item.capability === name);
    assert.ok(capability, `missing capability row: ${name}`);
    assert.strictEqual(capability.native, "pass", `${name} must pass after Phase 5`);
  }
});
