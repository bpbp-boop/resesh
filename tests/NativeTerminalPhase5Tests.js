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

test("current ABI retains composition hosting and stable annotation data", () => {
  assert.match(api, /AbiMajor\s*=\s*3/);
  assert.match(api, /AbiMinor\s*=\s*1/);
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
    assert.match(api, new RegExp(name), `${name} must be a required ABI 2.1 export`);
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

test("Enter-gated discovery survives prompt title updates and remains a fallback for incomplete shell marks", () => {
  const keyDown = surface.slice(
    surface.indexOf("private void OnTerminalKeyDown"),
    surface.indexOf("private void OnTerminalKeyUp")
  );
  assert.ok(keyDown.indexOf("BeginPromptDiscovery()") < keyDown.indexOf("_api.SendKeyEvent"));
  assert.match(surface, /Task\.Delay\(attempt == 0 \? 300 : 900/);
  assert.doesNotMatch(surface, /_titleEpoch/);
  assert.doesNotMatch(surface, /_exactShellMarksSeen/);
  assert.match(surface, /SettlePromptProbeAsync\(probe\.Id, cancellation\.Token\)/);
  assert.doesNotMatch(surface, /ExactCommand && mark\.Row == probeRow/);
  assert.doesNotMatch(surface, /_api\.GetMarks\(_terminal\)\.Any\(mark =>/);
  assert.match(panel, /\.GroupBy\(mark => mark\.Row\)[\s\S]*?\.Select\(MergeCommandMarks\)/);
  assert.match(panel, /MergeCommandMarks[\s\S]*?ApplicationCommand[\s\S]*?ExactCommand && HasStatus\(mark\)[\s\S]*?applicationMark with[\s\S]*?ExitCode = exactMark\.ExitCode/);
  assert.match(surface, /ForgetPromptProbe\(ulong probeId\)[\s\S]*?_osc3008Probes[\s\S]*?entry\.Value == probeId/);
  assert.match(surface, /ObserveOsc3008[\s\S]*?_api\.CreateApplicationMark/);
  assert.match(surface, /if \(type != "command"\)[\s\S]*?return/);
  assert.match(surface, /status is >= 0 and <= 255/);
  assert.match(surface, /exitKind is "failure" or "crash" or "interrupt" \? 1 : null/);
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
  assert.match(ruler, /ScrollByWheelDelta\(int delta\)/);
  assert.match(ruler, /_wheelDelta \+= delta/);
  assert.match(ruler, /var displayedTop = _pendingScrolls\.Count > 0 \? _pendingScrolls\.Last\(\)\.Target : _viewTop/);
  assert.match(ruler, /_pendingScrolls\.Enqueue\(\(correlationId, target\)\);[\s\S]*?_scrollBar\.Value = target/);
  assert.match(surface, /\(result & PointerHandled\) == 0[\s\S]*?_ruler\.ScrollByWheelDelta/);
  assert.match(surface, /Background = new SolidColorBrush\(Microsoft\.UI\.Colors\.Transparent\)/);
  assert.match(surface, /PointerWheelChanged \+= OnPointerWheelChanged/);
  assert.match(surface, /OriginalSource is not DependencyObject source[\s\S]*?!IsDescendantOrSelf\(source, _inputPanel\)/);
});

test("the native ruler uses a composition-safe TeachingTip for mark actions", () => {
  assert.match(ruler, /TeachingTip _markTip = new\(\)/);
  assert.match(ruler, /TeachingTipPlacementMode\.Left/);
  assert.match(ruler, /IsLightDismissEnabled = false/);
  assert.match(ruler, /_markTip\.Target = _markTipAnchor/);
  assert.match(ruler, /_markTipLayer\.Children\.Add\(_markTipAnchor\)/);
  assert.match(ruler, /Canvas\.SetTop\(_markTipAnchor, MarkTop\(mark\.Row, railHeight\)\)/);
  assert.match(ruler, /_markTip\.Closed \+= [\s\S]*?_markTipClosing = false;[\s\S]*?QueueOpenMarkPreview\(\)/);
  assert.match(ruler, /if \(_markTip\.IsOpen\)[\s\S]*?_markTipClosing = true;[\s\S]*?_markTip\.IsOpen = false;[\s\S]*?return;/);
  assert.match(ruler, /QueueOpenMarkPreview\(\)[\s\S]*?DispatcherQueue\.TryEnqueue\([\s\S]*?_markTipLayer\.UpdateLayout\(\);[\s\S]*?_markTip\.Target = _markTipAnchor;[\s\S]*?_markTip\.IsOpen = true/);
  assert.match(ruler, /AutomationId\(_annotationInput, "NativeTerminalAnnotations"\)/);
  assert.match(ruler, /PointerMovedEvent[\s\S]*?ShowMarkPreview\(nearest\)/);
  assert.match(ruler, /_markTip\.IsOpen = true/);
  assert.match(ruler, /CopyRequested\?\.Invoke\(_activeMarkId\)/);
  assert.doesNotMatch(ruler, /NativeTerminalMarkPopup|AppWindow|SetWindowLongPtr/);
  assert.match(surface, /SwapChainPanel _terminalPanel = new\(\)/);
  assert.doesNotMatch(surface, /_terminalPanel\.Background\s*=/);
  assert.match(surface, /Border _inputPanel = new\(\)[\s\S]*?Background = new SolidColorBrush\(Microsoft\.UI\.Colors\.Transparent\)/);
  assert.match(surface, /Children\.Add\(_terminalPanel\);[\s\S]*?Children\.Add\(_inputPanel\)/);
  assert.match(surface, /_terminalPanel\.IsHitTestVisible = false/);
  assert.match(surface, /SetInputEnabled\(bool enabled\)[\s\S]*?_inputPanel\.IsHitTestVisible = enabled/);
  assert.match(surface, /OnPointerPressed[\s\S]*?_ruler\.DismissMarkPreview\(\)/);
  assert.match(surface, /AttachSwapChainPanel/);
  assert.match(surface, /_ruler\.CopyRequested \+= CopyMarkOutput/);
  assert.match(surface, /return CopyToClipboard\(text, null, null\)/);
});

test("the commands panel floats over the terminal and remains virtualized", () => {
  assert.match(panel, /PreferredWidth = 400/);
  assert.match(panel, /MaximumHeight = 520/);
  assert.match(panel, /ListView _list = new\(\)/);
  assert.match(panel, /ItemsSource = commands/);
  assert.match(panel, /ItemTemplate = CommandTemplate/);
  assert.match(panel, /DataTemplate[\s\S]*?\{Binding Text\}/);
  assert.match(panel, /public string Text => _text \?\?= ReadText\(\)/);
  assert.match(panel, /JumpRequested/);
  assert.match(panel, /CopyRequested/);
  assert.match(panel, /VerticalAlignment = VerticalAlignment\.Top/);
  assert.match(panel, /BorderThickness = new Thickness\(1\)/);
  assert.match(panel, /CaptionTextBlockStyle/);
  assert.match(panel, /DesiredHeight = Math\.Min\(MaximumHeight, 40 \+ Math\.Max\(1, commands\.Length\) \* 28\)/);
  assert.match(panel, /ApplyTheme\(NativeTerminalApi\.TerminalTheme theme, string fontFamily\)/);
  assert.match(panel, /DefaultBackground[\s\S]*?DefaultForeground[\s\S]*?DefaultSelectionBackground/);
  assert.match(panel, /AnsiBrightRedIndex = 9/);
  assert.match(panel, /AnsiBrightGreenIndex = 10/);
  assert.match(panel, /_successBrush\.Color = NativeTerminalRuler\.FromColorRef\(theme\.ColorTable\[AnsiBrightGreenIndex\]\)/);
  assert.match(panel, /_failureBrush\.Color = NativeTerminalRuler\.FromColorRef\(theme\.ColorTable\[AnsiBrightRedIndex\]\)/);
  assert.match(panel, /_mark\.ExitCode == 0[\s\S]*?_mark\.Category == SuccessMarkCategory/);
  assert.match(panel, /_mark\.ExitCode is not null \|\| _mark\.Category == ErrorMarkCategory/);
  assert.match(panel, /public Brush StatusBrush => Succeeded \? _successBrush : Failed \? _failureBrush : _unknownBrush/);
  assert.match(panel, /ToolTipService\.ToolTip="\{Binding StatusName\}"/);
  assert.match(panel, /ButtonBackgroundPointerOver/);
  assert.match(panel, /ListViewItemBackgroundSelected/);
  assert.match(surface, /_commandsPanel\.Height = Math\.Min\([\s\S]*?_commandsPanel\.DesiredHeight/);
  assert.match(surface, /_commandsPanel\.Margin = new Thickness\(0, commandsTop, rulerWidth \+ 6, 0\)/);
  assert.match(surface, /_terminalPanel\.Margin = new Thickness\(0, findHeight, rulerWidth, 0\)/);
  assert.doesNotMatch(surface, /chromeWidth/);
  assert.match(surface, /_commandsPanel\.JumpRequested \+= ScrollToMark/);
  assert.match(surface, /_commandsPanel\.CopyRequested \+=[^\n]*CopyMarkOutput/);
  assert.match(surface, /_api\.ScrollToMark\(_terminal, markId\);[\s\S]*?ShowJumpHighlight\(markId\)/);
  assert.match(surface, /UpdateJumpHighlightBounds\(\)[\s\S]*?mark\.Row - _viewTop/);
  assert.match(surface, /_jumpHighlightTimer\.Interval = TimeSpan\.FromMilliseconds\(700\)/);
  assert.match(surface, /DefaultSelectionBackground[\s\S]*?_jumpHighlightBrush\.Color/);
  assert.match(surface, /mark\.ExitCode \?\? int\.MinValue/);
});

test("Phase 5 capability rows are complete", () => {
  for (const name of ["command marks", "overview ruler", "bookmarks", "commands panel"]) {
    const capability = capabilities.capabilities.find((item) => item.capability === name);
    assert.ok(capability, `missing capability row: ${name}`);
    assert.strictEqual(capability.native, "pass", `${name} must pass after Phase 5`);
  }
});
