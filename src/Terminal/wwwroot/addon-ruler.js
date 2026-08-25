/**
 * Annotated-scrollbar addon for xterm.js (Resesh's own, not vendored) — ROADMAP Phase 9.2.
 *
 * A 14px interactive overview ruler on the terminal's right edge, replacing both the
 * viewport's native scrollbar (hidden in terminal.html CSS) and xterm's built-in
 * render-only overview ruler. Paints a map of the whole scrollback:
 *   - left lane:  structure — command marks (Phase 9.4) under user bookmarks
 *                 (Ctrl+Shift+M toggles a bookmark on the cursor line)
 *   - right lane: content — highlight-rule hits (Phase 9.3) under search-match ticks
 *   - translucent viewport window doubling as the scroll thumb
 *
 * Interaction: click jumps (snapping to a nearby mark and flashing the target line),
 * drag scrubs like a scrollbar, wheel forwards to terminal scroll, hover shows a
 * tooltip (~150ms) with the region's line number, mark counts, and first match text.
 * When the hovered region holds a command mark the tooltip becomes interactive: a
 * short grace delay lets the pointer reach its "Jump to" and "Copy output" buttons,
 * which act on the region's nearest mark. The commands panel lists every command
 * mark — click a row to jump, or its copy button for that command's output. It is
 * opened by the host's native tab-strip button (a "toggleCommands" page message)
 * or Ctrl+Shift+O forwarded by the page; an open find bar shifts it down a step.
 * A copied "output" leads with the command's own prompt line (a paste reads like a
 * transcript), then the buffer text up to the next command mark,
 * soft wraps joined; trailing blank lines and empty-Enter prompt lines (recognized
 * by equality with a neighboring mark's prompt, never by shape alone — output like
 * "</html>" parses as a bare prompt shape) are dropped, and the live idle prompt
 * (the cursor's logical line) is excluded the same way.
 *
 * Data notes:
 *   - Match positions come from the addon's OWN line-level buffer scan (same query and
 *     case/regex flags as the find bar) — the search addon does not expose positions.
 *     Line granularity is all a ruler needs; a match spanning a wrap boundary marks the
 *     row where it starts. Scans run in 4096-line slices per animation frame so a 100k
 *     scrollback cannot hitch rendering; the ruler fills in progressively.
 *   - Bookmarks are xterm markers: they track scrollback trimming and reflow for free
 *     and dispose themselves when their line is trimmed away.
 *   - While a search is active, buffer writes re-run the scan (debounced) so trimming
 *     can't leave ticks pointing at shifted lines.
 *   - Highlight-rule hits (Phase 9.3) come from the addon's own whole-buffer index,
 *     maintained incrementally: completed lines are scanned as they arrive (onLineFeed
 *     kicks a budgeted idle-time pass) and a backfill pass indexes existing scrollback.
 *     The index is a compact side structure (line -> rule bitmask), not decorations.
 *     Lines are keyed by a trim-stable "virtual" number anchored to a sentinel marker;
 *     markers shift with scrollback trimming, so virtual = absolute + (anchor virtual -
 *     anchor marker line) stays constant for a given line. Resize reflow, rule changes,
 *     and losing the sentinel to a trim flood all invalidate the index and rebuild it,
 *     painting a faint veil over the not-yet-indexed span while catching up.
 *   - Command marks (Phase 9.4) have three sources feeding one lane:
 *       exact — OSC 133 (FinalTerm) sequences from an integrated shell. A/B remember
 *       the prompt line, C commits a mark there (a command actually ran), D attaches
 *       the exit code, which colors the tick (ok/fail). Shells that emit A/D but no C
 *       still get marks committed on D.
 *       stock context — OSC 3008 command IDs attach exact systemd-reported results
 *       to discovered marks, but do not replace discovery because no text is sent.
 *       discovered — for the fleet of VMs and network devices that will never get a
 *       custom bashrc: when the user presses Enter (the page forwards it from
 *       term.onData), a probe marker anchors the cursor row and, once the remote echo
 *       has settled, its line is tested against a prompt regex; a match with a
 *       command after the prompt becomes a neutral-colored mark. Enter-gating is
 *       what makes this safe: output that merely LOOKS like a prompt never coincides
 *       with the user pressing Enter on it. Wrapped commands walk back to the row the
 *       prompt started on. The first OSC 133 sequence in a session disables discovery
 *       — the shell knows better than the regex.
 *     Marks are xterm markers (trim-safe); tmux capture-pane replay preserves neither
 *     OSC 133 nor keystroke history, so the command lane starts fresh on reattach.
 *     Both sources also hand the command TEXT to the page through two hooks:
 *     onRunningCommand drives the tab's subtitle, showing what is running between
 *     prompt titles (those only refresh after a command ends) — 133;D reports the end,
 *     and for discovered commands the host treats the next prompt-shaped title as the
 *     end. onCommandMark is the separate per-mark feed Phase 6.2 agent detection reads:
 *     it never reports an end and is not epoch-gated, so agent evidence survives a
 *     command the subtitle discards.
 *   - Line timestamps (Phase 9.5) arrive from the native host with each SSH read,
 *     before its 16 ms output batch combines reads. A compact virtual-line map keeps
 *     them aligned through scrollback trimming. The page snapshots logical-line times
 *     around xterm reflow so a font or window-size change does not detach them.
 *   - The alternate buffer (vim/htop) has no scrollback: the ruler hides itself.
 */
(function () {
  "use strict";

  var WIDTH = 14;           // CSS px
  var SCAN_SLICE = 4096;    // lines per animation-frame slice
  var RESCAN_DEBOUNCE = 250;
  var HOVER_DELAY = 150;
  var SNAP_PX = 8;          // click snaps to a mark within this many CSS px
  var DRAG_THRESHOLD = 3;   // pointer movement below this is a click, not a drag
  var FLASH_MS = 700;
  // Match VS Code's scrollbar timing: quick reveal, slow fade after pointer exit.
  var THUMB_SHOW_MS = 100;
  var THUMB_HIDE_MS = 800;

  var HL_MAX_RULES = 32;    // bitmask width; overview rules beyond this are ignored
  var HL_SLICE = 2048;      // indexer lines per pass when no idle deadline is available
  var HL_IDLE_MIN_MS = 3;   // stop an idle pass when less than this remains
  var HL_REANCHOR_GAP = 4096; // re-anchor the sentinel once the cursor is this far past it
  var HL_TICK_ALPHA = 0.8;  // slight dim keeps opaque search ticks dominant on top

  var CMD_ECHO_SETTLE_MS = 300; // Enter -> probe evaluation: wait for the echo round trip
  var CMD_ECHO_RETRY_MS = 900;  // one retry for laggy links before the probe gives up

  var TIME_REANCHOR_GAP = 4096;

  // Prompt shapes for discovered command marks: bracketed, compact, or a spaced
  // user@host + cwd Bash prompt. Then comes an optional space and a non-space (the
  // command; an empty prompt never marks). Fancy unicode prompts belong to hosts with
  // OSC 133 shell integration. The leading "PS …>" alternative covers PowerShell
  // (cmd.exe's "C:\dir>" already matches the space-free body).
  // Keep spaced prompts narrow: user@host must precede the cwd. This accepts common
  // coloured Bash prompts such as "user@host ~/work $" without admitting arbitrary
  // output lines that contain spaces.
  var UNIX_SPACED_PROMPT_BODY =
    "[^@\\s$#%>]{1,100}@[^\\s$#%>]{1,100}\\s+[^\\r\\n$#%>]{1,160}?\\s*[$#%>]";
  var CMD_PROMPT_BODY = "(?:PS [^\\n]{0,200}>|" + UNIX_SPACED_PROMPT_BODY
    + "|(?:\\[[^\\]]{1,100}\\]|[^\\s$#%>]{0,100})[$#%>])";
  var CMD_PROMPT_RE = new RegExp("^" + CMD_PROMPT_BODY + "\\s?\\S");
  // Same shapes, capturing prompt and command separately: the tooltip card, agent
  // detection, and the running-command title all slice the command out with this.
  var CMD_SPLIT_RE = new RegExp("^(" + CMD_PROMPT_BODY + ")\\s?(.*)$");
  // Default Windows prompts expose the current directory but do not update the console
  // title. Keep this deliberately narrow: only PowerShell and absolute drive/UNC paths.
  var WINDOWS_IDLE_PROMPT_RE = /^(?:PS )?((?:[A-Za-z]:[\\/]|\\\\)[^\r\n>]*)>\s*$/;
  // A spaced Bash prompt does not normally emit OSC 0/2. Reading its cwd lets the tab
  // leave the endpoint fallback while idle and clears a discovered command on return.
  var UNIX_SPACED_IDLE_PROMPT_RE =
    /^[^@\s$#%>]{1,100}@[^\s$#%>]{1,100}\s+([^\r\n$#%>]{1,160}?)\s*[$#%>]\s*$/;
  // CentOS/RHEL commonly wraps that same user, host, and cwd shape in brackets:
  // "[root@server ~]#". Capture only the cwd so the tab never displays "~]".
  var UNIX_BRACKETED_IDLE_PROMPT_RE =
    /^\[[^@\]\s]{1,100}@[^\]\s]{1,100}\s+([^\]\r\n]{1,160}?)\]\s*[$#%>]\s*$/;
  // Nokia SR OS MD-CLI uses a two-line prompt. The first line carries the current
  // cli-path and candidate mode; the second carries the active CPM, user, and node.
  // Requiring both lines keeps a bracketed output line from being mistaken for a prompt.
  var NOKIA_MD_CLI_PROMPT_RE = /^[A-Z]:[^@\s]+@[^\s#]+#\s*$/;
  var NOKIA_MD_CLI_CONTEXT_RE = /^(\*)?(?:\((gl|ex|pr|ro)\)\s*)?\[((?:(gl|ex|pr|ro):)?[^\]\r\n]{0,500})\]$/;
  var NOKIA_MD_CLI_MODES = {
    gl: "global", ex: "exclusive", pr: "private", ro: "read-only"
  };
  // Junos uses user@host> in operational mode. That shape alone is not vendor-safe
  // (PAN-OS can look the same), so icon detection also requires a JUNOS login banner,
  // a routing-engine role marker, or the configuration hierarchy banner.
  var JUNOS_PROMPT_RE = /^[^@\s]+@[^\s#>]+([#>])\s*$/;
  var JUNOS_EDIT_CONTEXT_RE = /^(?:\{([^}\r\n]{1,100})\}\s*)?\[edit(?:\s+([^\]\r\n]{1,400}))?\]\s*$/;
  var JUNOS_ROLE_RE = /^\{((?:master|backup|primary|secondary)(?::[^}\s]+)?)\}\s*$/i;
  var JUNOS_BANNER_RE = /^---\s*JUNOS\b/i;
  // IOS and IOS XE share hostname[(mode)]#/>, while IOS XR prefixes the prompt
  // with its active route-processor location. A bare hostname# is also a common root
  // shell, so it needs a Cisco banner/platform hint; a (config-*) mode is useful generic
  // network context even without one, but does not suggest the Cisco icon by itself.
  var CISCO_IOS_PROMPT_RE = /^([A-Za-z0-9][A-Za-z0-9._-]{0,100})(?:\(([^()\r\n]{1,100})\))?([#>])\s*$/;
  var CISCO_XR_PROMPT_RE = /^(RP\/\d+\/(?:RP|RSP)\d+\/CPU\d+):([^()\s#>]+)(?:\(([^()\r\n]{1,100})\))?([#>])\s*$/i;
  var CISCO_BANNER_RE = /\b(?:Cisco IOS(?: XE| XR)? Software|Cisco Internetwork Operating System Software)\b/i;
  var CISCO_SUBMODE_NAMES = {
    "if": "interface", "if-pre": "preconfigured interface", "subif": "subinterface",
    "router": "routing", "line": "line", "vlan": "VLAN", "vrf": "VRF",
    "std-nacl": "standard ACL", "ext-nacl": "extended ACL", "bgp": "BGP",
    "bgp-af": "BGP address family", "bgp-nbr": "BGP neighbor",
    "bgp-nbr-af": "BGP neighbor address family"
  };

  function recentLineMatches(buf, start, pattern, limit) {
    for (var scan = start - 1; scan >= Math.max(0, start - limit); scan--) {
      var scanLine = buf.getLine(scan);
      if (scanLine && pattern.test(scanLine.translateToString(true))) return true;
    }
    return false;
  }

  function ciscoModeParts(mode, terminator, isXr) {
    if (!mode) return [terminator === ">" ? "user EXEC" : (isXr ? "EXEC" : "privileged EXEC")];
    if (mode === "admin") return ["administration"];
    if (mode === "admin-config") return ["administration", "configure"];
    if (mode.indexOf("admin-config-") === 0) {
      var adminSubmode = mode.slice(13);
      return ["administration", "configure",
        CISCO_SUBMODE_NAMES[adminSubmode] || adminSubmode.replace(/-/g, " ")];
    }
    if (mode === "config") return ["configure"];
    if (mode.indexOf("config-") === 0) {
      var submode = mode.slice(7);
      return ["configure", CISCO_SUBMODE_NAMES[submode] || submode.replace(/-/g, " ")];
    }
    return [mode.replace(/-/g, " ")];
  }

  function RulerAddon() {
    this._term = null;
    this._disposables = [];
    this._strip = null;
    this._canvas = null;
    this._tooltip = null;
    this._thumb = null;
    this._resizeObserver = null;

    this._colors = {
      background: "#0c0c0c", border: "#333333", thumb: "rgba(255,255,255,0.10)",
      match: "#7f8ea3", activeMatch: "#f2cc60", bookmark: "#61d6d6",
      flash: "rgba(242,204,96,0.28)", pending: "rgba(255,255,255,0.06)",
      cmdOk: "#2ea043", cmdFail: "#ff5555", cmdUnknown: "#9e9e9e",
      tooltipBg: "#1e1e2b", tooltipFg: "#e6edf3", tooltipMuted: "#a7a7b5",
      tooltipBorder: "#3a3a50"
    };

    this._search = null;      // { query, caseSensitive, regex }
    this._matchLines = [];    // absolute buffer rows, ascending
    this._activeLine = -1;
    this._scanGeneration = 0;
    this._rescanTimer = null;

    this._bookmarks = [];     // [{ marker }] — marker.line stays current through trim/reflow

    this._cmdMarks = [];      // [{ marker, exit, src, text }] src "osc"|"guess"; exit int or null
    this._cmdOscSeen = false; // a shell spoke OSC 133: discovery defers to it from then on
    this._cmdPromptLine = -1; // absolute line of the last OSC 133;A/B prompt start
    this._cmdPromptCol = -1;  // cursor column at OSC 133;B — where the typed command starts
    this._cmdPending = null;  // mark committed by C, awaiting its D exit code
    this._cmdObserver = null; // page hook: commands as they are marked (agent detection)
    this._cmdPanel = null;    // commands panel: every command mark as a clickable list
    this._cmdPanelList = null;
    this._cmdPanelCount = null;
    this._cmdPanelOpen = false;
    this._cmdPanelRefreshQueued = false;
    this._tooltipCommand = null;   // command entry the tooltip's action buttons act on
    this._tooltipHideTimer = null; // grace period so the pointer can reach those buttons
    this._cmdEnterProbes = []; // Enter markers waiting for echo / an OSC 3008 command ID
    this._osc3008Commands = new Map(); // bounded command ID -> probe, mark, and result
    this.onRunningCommand = null; // page hook: (text, epoch?) on start, ("") on 133;D
    this.onContext = null; // page hook: bounded raw OSC 3008 payload for native validation
    this.onPromptContext = null; // page hook: (label, platform?) when a known prompt is idle
    this.onCommandsPanelChanged = null; // page hook: (open) — the host's button mirrors it
    this._lastPromptSignature = null;
    this._promptPlatform = null; // strong prompt evidence persists across later mode changes

    this._timeWrites = [];    // serialized xterm writes: { data, unixMs|null }
    this._timeWriteHead = 0;  // avoids Array.shift() copying a queued backlog on every write
    this._timeWriteActive = false;
    this._timeActiveUnixMs = null;
    this._timeIndex = new Map(); // virtual logical-line start -> Unix milliseconds
    this._timeAnchor = null;  // { marker, virtual }, same trim-safe coordinate scheme as highlights
    this._timePruneAt = 0;
    this._timeReflowSnapshot = null;

    this._hlRules = [];       // compiled overview rules: { id, name, re, color }
    this._hlIndex = new Map();// virtual line -> bitmask of _hlRules indexes
    this._hlAnchor = null;    // { marker, virtual } — sentinel for the virtual coordinates
    this._hlFrontier = 0;     // next virtual line the indexer will scan
    this._hlPruneAt = 0;      // buffer-top virtual at the last stale-entry prune
    this._hlScanScheduled = false;

    this._paintQueued = false;
    this._drag = null;        // { pointerId, startY, moved }
    this._windowBlurHandler = null;
    this._hoverTimer = null;
    this._flash = null;       // { deco, marker, timer }
    this._isSplit = false;
    this._isGroupFocused = true;
    this._isPointerOver = false;
  }

  // Hover styling for the ruler's interactive chrome (tooltip action buttons and
  // the commands panel) needs real CSS rules; colors come from the active ruler
  // palette through custom properties set on the host element. The panel's open
  // control is the host's native tab-strip button, not page chrome.
  var CHROME_CSS =
    ".scroll-ruler-panel{position:absolute;top:8px;right:" + (WIDTH + 6) + "px;z-index:32;" +
      "display:none;flex-direction:column;width:400px;max-width:70%;" +
      "max-height:calc(100% - 28px);overflow:hidden;border-radius:6px;" +
      "box-shadow:0 6px 18px rgba(0,0,0,0.35);background:var(--sr-bg);color:var(--sr-fg);" +
      "border:1px solid var(--sr-border);" +
      "font-family:'Cascadia Mono',Consolas,monospace;font-size:12px;line-height:16px;}" +
    ".scroll-ruler-panel .srp-head{display:flex;align-items:center;gap:6px;flex:none;" +
      "padding:5px 6px 5px 10px;border-bottom:1px solid var(--sr-border);color:var(--sr-muted);" +
      "font-family:'Segoe UI',sans-serif;}" +
    ".scroll-ruler-panel .srp-title{font-weight:600;color:var(--sr-fg);}" +
    ".scroll-ruler-panel .srp-close{margin-left:auto;width:20px;height:20px;border:none;" +
      "border-radius:3px;background:transparent;color:var(--sr-muted);font-size:11px;cursor:pointer;}" +
    ".scroll-ruler-panel .srp-close:hover{background:rgba(128,128,128,0.25);color:var(--sr-fg);}" +
    ".scroll-ruler-panel .srp-list{overflow-y:auto;overflow-x:hidden;padding:3px 0;}" +
    ".scroll-ruler-panel .srp-empty{padding:8px 10px;color:var(--sr-muted);}" +
    ".scroll-ruler-panel .srp-row{display:flex;align-items:center;gap:7px;" +
      "padding:2px 6px 2px 10px;cursor:pointer;}" +
    ".scroll-ruler-panel .srp-row:hover{background:rgba(128,128,128,0.16);}" +
    ".scroll-ruler-panel .srp-dot{flex:none;width:7px;height:7px;border-radius:50%;}" +
    ".scroll-ruler-panel .srp-cmd{flex:1 1 auto;white-space:nowrap;overflow:hidden;" +
      "text-overflow:ellipsis;}" +
    ".scroll-ruler-panel .srp-time{flex:none;color:var(--sr-muted);font-size:11px;}" +
    ".scroll-ruler-panel .srp-copy{flex:none;visibility:hidden;min-width:20px;height:18px;" +
      "border:none;border-radius:3px;background:transparent;color:var(--sr-muted);" +
      "font-size:12px;cursor:pointer;padding:0 3px;font-family:inherit;}" +
    ".scroll-ruler-panel .srp-row:hover .srp-copy{visibility:visible;}" +
    ".scroll-ruler-panel .srp-copy:hover{background:rgba(128,128,128,0.3);color:var(--sr-fg);}" +
    ".scroll-ruler-tooltip .srt-actions{display:flex;gap:6px;margin-top:5px;}" +
    ".scroll-ruler-tooltip .srt-btn{border:1px solid var(--sr-border);border-radius:4px;" +
      "padding:1px 8px;background:transparent;color:var(--sr-fg);font-family:inherit;" +
      "font-size:11px;line-height:15px;cursor:pointer;}" +
    ".scroll-ruler-tooltip .srt-btn:hover{background:rgba(128,128,128,0.25);}";

  RulerAddon.prototype.activate = function (term) {
    var self = this;
    this._term = term;

    if (!document.getElementById("scroll-ruler-style")) {
      var chromeStyle = document.createElement("style");
      chromeStyle.id = "scroll-ruler-style";
      chromeStyle.textContent = CHROME_CSS;
      document.head.appendChild(chromeStyle);
    }

    var strip = document.createElement("div");
    strip.className = "scroll-ruler";
    strip.style.cssText = "position:absolute;top:0;right:0;bottom:0;width:" + WIDTH +
      "px;z-index:30;cursor:default;user-select:none;";
    var canvas = document.createElement("canvas");
    canvas.style.cssText = "position:absolute;top:0;left:0;width:100%;height:100%;";
    strip.appendChild(canvas);
    var thumb = document.createElement("div");
    thumb.className = "scroll-ruler-thumb";
    thumb.style.cssText = "position:absolute;left:0;width:100%;pointer-events:none;" +
      "opacity:0;transition:opacity " + THUMB_HIDE_MS + "ms linear;";
    strip.appendChild(thumb);
    // xterm's element is only as tall as its whole character rows, which can leave
    // unused pixels below it after fitting. Mount the ruler in the full-height host
    // so the scrollbar reaches the actual bottom edge of the content pane.
    var host = term.element.parentElement || term.element;
    host.appendChild(strip);
    this._strip = strip;
    this._canvas = canvas;
    this._thumb = thumb;

    var tooltip = document.createElement("div");
    tooltip.className = "scroll-ruler-tooltip";
    tooltip.style.cssText = "position:absolute;right:" + (WIDTH + 6) +
      "px;display:none;z-index:31;max-width:420px;padding:5px 10px;border-radius:6px;" +
      "font-family:'Cascadia Mono',Consolas,monospace;font-size:12px;line-height:16px;" +
      "white-space:normal;overflow:hidden;box-shadow:0 6px 18px rgba(0,0,0,0.35);" +
      "pointer-events:none;";
    // Only a tooltip with action buttons gets pointer-events back (in _showTooltip);
    // the purely informational card must never eat clicks aimed at the terminal.
    tooltip.addEventListener("pointerenter", function () { self._cancelTooltipHide(); });
    tooltip.addEventListener("pointerleave", function () { self._scheduleTooltipHide(); });
    host.appendChild(tooltip);
    this._tooltip = tooltip;

    var panel = document.createElement("div");
    panel.className = "scroll-ruler-panel";
    var panelHead = document.createElement("div");
    panelHead.className = "srp-head";
    var panelTitle = document.createElement("span");
    panelTitle.className = "srp-title";
    panelTitle.textContent = "Commands";
    var panelCount = document.createElement("span");
    panelCount.className = "srp-count";
    var panelClose = document.createElement("button");
    panelClose.className = "srp-close";
    panelClose.title = "Close";
    panelClose.textContent = "✕";
    panelClose.addEventListener("click", function () {
      self.toggleCommandsPanel(false);
      self._term.focus();
    });
    panelHead.appendChild(panelTitle);
    panelHead.appendChild(panelCount);
    panelHead.appendChild(panelClose);
    var panelList = document.createElement("div");
    panelList.className = "srp-list";
    panel.appendChild(panelHead);
    panel.appendChild(panelList);
    host.appendChild(panel);
    this._cmdPanel = panel;
    this._cmdPanelList = panelList;
    this._cmdPanelCount = panelCount;

    this._applyChromeTheme();

    this._disposables.push(term.onScroll(function () { self._queuePaint(); }));
    this._disposables.push(term.onResize(function () {
      self._queuePaint();
      if (self._search) self._scheduleRescan();
      self._hlRebuild(); // reflow rewraps rows; absolute line numbers all move
    }));
    this._disposables.push(term.onWriteParsed(function () {
      self._queuePaint();
      if (self._search) self._scheduleRescan();
      self._reportPromptContext();
    }));
    this._disposables.push(term.onLineFeed(function () {
      self._timeStampCurrent();
      self._hlKick();
    }));
    if (term.parser && term.parser.registerOscHandler) {
      this._disposables.push(term.parser.registerOscHandler(7, function (data) {
        try {
          if (self.onWorkingDirectory) self.onWorkingDirectory(String(data || "").slice(0, 2048));
        } catch (err) {
          if (window.__pageTrace) window.__pageTrace("ruler osc7: " + (err && err.message));
        }
        return true;
      }));
      this._disposables.push(term.parser.registerOscHandler(133, function (data) {
        try {
          self._onOsc133(data);
        } catch (err) {
          if (window.__pageTrace) window.__pageTrace("ruler osc133: " + (err && err.message));
        }
        return true;
      }));
      this._disposables.push(term.parser.registerOscHandler(3008, function (data) {
        try {
          var value = String(data || "");
          var raw = value.length <= 4096 ? value : "";
          self._onOsc3008(raw);
          if (self.onContext) self.onContext(raw);
        } catch (err) {
          if (window.__pageTrace) window.__pageTrace("ruler osc3008: " + (err && err.message));
        }
        return true;
      }));
    }
    // Covers alt-buffer enter/leave and anything else that repaints without scrolling.
    // The kick also resumes a highlight-index backfill paused by the alternate buffer.
    this._disposables.push(term.onRender(function () {
      self._queuePaint();
      self._hlKick();
    }));

    strip.addEventListener("pointerenter", function () {
      self._isPointerOver = true;
      self._cancelTooltipHide();
      self._syncThumbVisibility();
      self._queuePaint();
    });
    strip.addEventListener("pointerdown", function (e) { self._onPointerDown(e); });
    strip.addEventListener("pointermove", function (e) { self._onPointerMove(e); });
    strip.addEventListener("pointerup", function (e) { self._onPointerUp(e); });
    strip.addEventListener("pointercancel", function (e) { self._cancelDrag(e.pointerId); });
    strip.addEventListener("lostpointercapture", function (e) {
      self._cancelDrag(e.pointerId, false);
    });
    strip.addEventListener("pointerleave", function () {
      self._isPointerOver = false;
      // Grace delay instead of an instant hide: the pointer crosses a 6px gap on
      // its way from the strip to an interactive tooltip's action buttons.
      self._scheduleTooltipHide();
      self._syncThumbVisibility();
      self._queuePaint();
    });
    strip.addEventListener("wheel", function (e) {
      e.preventDefault();
      var lines = Math.round(e.deltaY / 40) || (e.deltaY > 0 ? 1 : -1);
      self._term.scrollLines(lines);
    }, { passive: false });

    // WebView2 can lose pointer capture when its host window deactivates. Chromium does
    // not always deliver the matching pointerup in that case, so do not retain a drag
    // when the user leaves the window and later returns.
    this._windowBlurHandler = function () { self._cancelDrag(); };
    window.addEventListener("blur", this._windowBlurHandler);

    this._resizeObserver = new ResizeObserver(function () { self._queuePaint(); });
    this._resizeObserver.observe(strip);

    this._queuePaint();
    this._syncThumbVisibility();
  };

  RulerAddon.prototype.dispose = function () {
    this._cancelDrag();
    if (this._windowBlurHandler) {
      window.removeEventListener("blur", this._windowBlurHandler);
      this._windowBlurHandler = null;
    }
    for (var i = 0; i < this._disposables.length; i++) this._disposables[i].dispose();
    this._disposables = [];
    for (var b = 0; b < this._bookmarks.length; b++) this._bookmarks[b].marker.dispose();
    this._bookmarks = [];
    var cmdMarks = this._cmdMarks.slice(); // onDispose handlers splice the live array
    for (var m = 0; m < cmdMarks.length; m++) cmdMarks[m].marker.dispose();
    this._cmdMarks = [];
    this._cmdPending = null;
    this._timeWrites = [];
    this._timeWriteHead = 0;
    this._timeWriteActive = false;
    this._timeActiveUnixMs = null;
    this._timeIndex.clear();
    if (this._timeAnchor) { var timeAnchor = this._timeAnchor; this._timeAnchor = null; timeAnchor.marker.dispose(); }
    this._hlRules = [];
    this._hlIndex.clear();
    if (this._hlAnchor) { var anchor = this._hlAnchor; this._hlAnchor = null; anchor.marker.dispose(); }
    this._clearFlash();
    this._cancelTooltipHide();
    if (this._resizeObserver) this._resizeObserver.disconnect();
    if (this._strip && this._strip.parentElement) this._strip.parentElement.removeChild(this._strip);
    if (this._tooltip && this._tooltip.parentElement) this._tooltip.parentElement.removeChild(this._tooltip);
    if (this._cmdPanel && this._cmdPanel.parentElement) this._cmdPanel.parentElement.removeChild(this._cmdPanel);
    this._thumb = null;
    this._cmdPanel = null;
    this._cmdPanelList = null;
    this._cmdPanelCount = null;
    this._cmdPanelOpen = false;
    this._tooltipCommand = null;
    this._term = null;
  };

  /** Partial override of the color set; keys as in the constructor default. */
  RulerAddon.prototype.setTheme = function (colors) {
    for (var k in colors) this._colors[k] = colors[k];
    this._applyChromeTheme();
    this._queuePaint();
    this._queuePanelRefresh(); // open-panel row dots repaint in the new palette
  };

  /** Feed the ruler palette to its DOM chrome. */
  RulerAddon.prototype._applyChromeTheme = function () {
    var c = this._colors;
    if (this._thumb) this._thumb.style.backgroundColor = c.thumb;
    var hostEl = this._strip && this._strip.parentElement;
    if (!hostEl || !hostEl.style || !hostEl.style.setProperty) return;
    hostEl.style.setProperty("--sr-bg", c.tooltipBg);
    hostEl.style.setProperty("--sr-fg", c.tooltipFg);
    hostEl.style.setProperty("--sr-muted", c.tooltipMuted);
    hostEl.style.setProperty("--sr-border", c.tooltipBorder);
  };

  /** Full presentation in one group; quieter marks in split mode.
   * Hover restores full mark detail without changing the rail geometry. */
  RulerAddon.prototype.setPresentation = function (isSplit, isGroupFocused) {
    this._isSplit = isSplit === true;
    this._isGroupFocused = isGroupFocused !== false;
    this._queuePaint();
  };

  // ---- host-ingest timestamps (Phase 9.5) ----

  /** Serialize all terminal writes so the active host timestamp is exact while xterm's
   * asynchronous parser raises onLineFeed. Pass null for app-generated notices. */
  RulerAddon.prototype.writeOutput = function (data, unixMs) {
    this._timeWrites.push({
      data: data,
      unixMs: typeof unixMs === "number" && isFinite(unixMs) ? unixMs : null
    });
    this._timeDrainWrites();
  };

  RulerAddon.prototype._timeDrainWrites = function () {
    if (this._timeWriteActive || !this._term || this._timeWriteHead >= this._timeWrites.length) return;
    var self = this;
    var next = this._timeWrites[this._timeWriteHead++];
    if (this._timeWriteHead === this._timeWrites.length) {
      this._timeWrites = [];
      this._timeWriteHead = 0;
    }
    this._timeWriteActive = true;
    this._timeActiveUnixMs = next.unixMs;
    this._timeStampCurrent();
    this._term.write(next.data, function () {
      // Capture a partial line or cursor movement which did not raise onLineFeed.
      self._timeStampCurrent();
      self._timeActiveUnixMs = null;
      self._timeWriteActive = false;
      self._timeDrainWrites();
    });
  };

  RulerAddon.prototype._timeStampCurrent = function () {
    if (this._timeActiveUnixMs === null || !this._term) return;
    var buf = this._term.buffer.active;
    if (buf.type === "alternate") return;
    this._timeStampLine(buf.baseY + buf.cursorY, this._timeActiveUnixMs);
  };

  /** Timestamp the logical line containing abs. Wrapped display rows inherit the time
   * from their first row, so the index stays compact and reflow has one value to move. */
  RulerAddon.prototype._timeStampLine = function (abs, unixMs) {
    var buf = this._term.buffer.active;
    var line = buf.getLine(abs);
    while (line && line.isWrapped && abs > 0) {
      abs--;
      line = buf.getLine(abs);
    }
    var anchor = this._timeEnsureAnchor();
    if (!anchor) return;
    var offset = anchor.virtual - anchor.marker.line;
    this._timeIndex.set(abs + offset, unixMs);

    var topVirt = offset;
    if (topVirt > this._timePruneAt + TIME_REANCHOR_GAP) {
      this._timePruneAt = topVirt;
      var dead = [];
      this._timeIndex.forEach(function (value, virt) { if (virt < topVirt) dead.push(virt); });
      for (var i = 0; i < dead.length; i++) this._timeIndex.delete(dead[i]);
    }
  };

  RulerAddon.prototype._timeForLine = function (abs) {
    if (!this._timeAnchor || !this._term) return null;
    var buf = this._term.buffer.active;
    var line = buf.getLine(abs);
    while (line && line.isWrapped && abs > 0) {
      abs--;
      line = buf.getLine(abs);
    }
    var virt = abs + this._timeAnchor.virtual - this._timeAnchor.marker.line;
    var value = this._timeIndex.get(virt);
    return typeof value === "number" ? value : null;
  };

  RulerAddon.prototype._timeEnsureAnchor = function () {
    var term = this._term;
    var buf = term.buffer.active;
    if (this._timeAnchor) {
      if (buf.baseY + buf.cursorY - this._timeAnchor.marker.line > TIME_REANCHOR_GAP) {
        var next = term.registerMarker(0);
        if (next) {
          var virt = next.line + this._timeAnchor.virtual - this._timeAnchor.marker.line;
          var old = this._timeAnchor;
          this._timeAnchor = { marker: next, virtual: virt };
          this._timeWatchAnchor(next);
          old.marker.dispose();
        }
      }
      return this._timeAnchor;
    }
    var marker = term.registerMarker(0);
    if (!marker) return null;
    this._timeAnchor = { marker: marker, virtual: marker.line };
    this._timeWatchAnchor(marker);
    return this._timeAnchor;
  };

  RulerAddon.prototype._timeWatchAnchor = function (marker) {
    var self = this;
    marker.onDispose(function () {
      if (self._timeAnchor && self._timeAnchor.marker === marker) {
        // A single parser flood outran re-anchoring. Old coordinates can no longer be
        // related to the buffer safely, so discard them instead of showing wrong times.
        self._timeAnchor = null;
        self._timeIndex.clear();
        self._timePruneAt = 0;
      }
    });
  };

  /** Called immediately before fit.fit(). Preserve one timestamp (or null) per
   * logical line plus the cursor's ordinal. The cursor anchor accounts for any blank
   * viewport rows added or removed, or old scrollback trimmed, during reflow. */
  RulerAddon.prototype.captureTimestampReflow = function () {
    if (!this._term || !this._timeAnchor) { this._timeReflowSnapshot = null; return; }
    var buf = this._term.buffer.active;
    if (buf.type === "alternate") { this._timeReflowSnapshot = null; return; }
    var values = [];
    var cursorAbs = buf.baseY + buf.cursorY;
    var cursorLogical = 0;
    for (var i = 0; i < buf.length; i++) {
      var line = buf.getLine(i);
      if (line && !line.isWrapped) {
        if (i <= cursorAbs) cursorLogical = values.length;
        values.push(this._timeForLine(i));
      }
    }
    this._timeReflowSnapshot = { values: values, cursorLogical: cursorLogical };
  };

  RulerAddon.prototype.restoreTimestampReflow = function () {
    var snapshot = this._timeReflowSnapshot;
    this._timeReflowSnapshot = null;
    if (!snapshot || !this._term) return;

    this._timeIndex.clear();
    this._timePruneAt = 0;
    if (this._timeAnchor) {
      var old = this._timeAnchor;
      this._timeAnchor = null;
      old.marker.dispose();
    }

    var buf = this._term.buffer.active;
    var starts = [];
    var cursorAbs = buf.baseY + buf.cursorY;
    var cursorLogical = 0;
    for (var i = 0; i < buf.length; i++) {
      var line = buf.getLine(i);
      if (!line || line.isWrapped) continue;
      if (i <= cursorAbs) cursorLogical = starts.length;
      starts.push(i);
    }
    var shift = cursorLogical - snapshot.cursorLogical;
    for (var logical = 0; logical < snapshot.values.length; logical++) {
      var target = logical + shift;
      var unixMs = snapshot.values[logical];
      if (unixMs !== null && target >= 0 && target < starts.length)
        this._timeStampLine(starts[target], unixMs);
    }
  };

  RulerAddon.prototype._formatTimestamp = function (unixMs, nowMs) {
    if (typeof unixMs !== "number" || !isFinite(unixMs)) return null;
    var date = new Date(unixMs);
    var clock = String(date.getHours()).padStart(2, "0") + ":" +
      String(date.getMinutes()).padStart(2, "0");
    var elapsed = Math.max(0, (typeof nowMs === "number" ? nowMs : Date.now()) - unixMs);
    var seconds = Math.floor(elapsed / 1000);
    var relative;
    if (seconds < 60) relative = "now";
    else if (seconds < 3600) relative = Math.floor(seconds / 60) + "m ago";
    else if (seconds < 86400) relative = Math.floor(seconds / 3600) + "h ago";
    else relative = Math.floor(seconds / 86400) + "d ago";
    return { clock: clock, relative: relative };
  };

  // ---- search source ----

  /** Called by the find bar on every find action. Re-scans the buffer for line-level
   * match positions and reads the active match from the selection the search addon set. */
  RulerAddon.prototype.notifySearch = function (query, caseSensitive, regex) {
    if (!query) { this.clearSearch(); return; }
    this._search = { query: query, caseSensitive: caseSensitive, regex: regex };
    var sel = this._term.getSelectionPosition();
    this._activeLine = sel ? sel.start.y : -1;
    this._startScan();
  };

  RulerAddon.prototype.clearSearch = function () {
    this._search = null;
    this._matchLines = [];
    this._activeLine = -1;
    this._scanGeneration++;
    if (this._rescanTimer) { clearTimeout(this._rescanTimer); this._rescanTimer = null; }
    this._queuePaint();
  };

  RulerAddon.prototype._scheduleRescan = function () {
    var self = this;
    if (this._rescanTimer) return;
    this._rescanTimer = setTimeout(function () {
      self._rescanTimer = null;
      if (self._search) self._startScan();
    }, RESCAN_DEBOUNCE);
  };

  RulerAddon.prototype._startScan = function () {
    var self = this;
    var term = this._term;
    var s = this._search;
    var generation = ++this._scanGeneration;

    var matcher;
    if (s.regex) {
      try {
        matcher = new RegExp(s.query, s.caseSensitive ? "" : "i");
      } catch (err) {
        this._matchLines = [];
        this._queuePaint();
        return;
      }
    } else {
      var needle = s.caseSensitive ? s.query : s.query.toLowerCase();
      matcher = null;
    }

    var found = [];
    var line = 0;

    function slice() {
      if (generation !== self._scanGeneration || !self._term) return;
      var buf = self._term.buffer.active;
      if (buf.type === "alternate") { self._matchLines = []; self._queuePaint(); return; }
      var end = Math.min(line + SCAN_SLICE, buf.length);
      for (; line < end; line++) {
        var bufLine = buf.getLine(line);
        if (!bufLine) continue;
        var text = bufLine.translateToString(true);
        if (matcher) {
          if (matcher.test(text)) found.push(line);
        } else {
          if ((s.caseSensitive ? text : text.toLowerCase()).indexOf(needle) >= 0) found.push(line);
        }
      }
      self._matchLines = found;
      self._queuePaint(); // progressive fill while long scans catch up
      if (line < buf.length) requestAnimationFrame(slice);
    }
    requestAnimationFrame(slice);
  };

  // ---- bookmarks ----

  /** Toggle a bookmark on the cursor's line. Returns true if one was added. */
  RulerAddon.prototype.toggleBookmark = function () {
    var term = this._term;
    var buf = term.buffer.active;
    if (buf.type === "alternate") return false;
    var cursorLine = buf.baseY + buf.cursorY;

    for (var i = 0; i < this._bookmarks.length; i++) {
      if (this._bookmarks[i].marker.line === cursorLine) {
        this._bookmarks[i].marker.dispose(); // onDispose handler removes the entry
        return false;
      }
    }

    var marker = term.registerMarker(0);
    if (!marker) return false;
    var self = this;
    var entry = { marker: marker };
    marker.onDispose(function () {
      var idx = self._bookmarks.indexOf(entry);
      if (idx >= 0) self._bookmarks.splice(idx, 1);
      self._queuePaint();
    });
    this._bookmarks.push(entry);
    this._queuePaint();
    return true;
  };

  // ---- command marks (Phase 9.4) ----

  /** OSC 133 (FinalTerm shell integration): A/B = prompt start, C = command output
   * starts (a command actually ran), D;exit = command finished. */
  RulerAddon.prototype._onOsc133 = function (data) {
    var buf = this._term.buffer.active;
    if (buf.type === "alternate") return;
    this._cmdOscSeen = true;
    var parts = String(data).split(";");
    var kind = parts[0];
    if (kind === "A" || kind === "B") {
      this._cmdPromptLine = buf.baseY + buf.cursorY;
      // B fires with the prompt painted and the cursor sitting at the input start;
      // A is the prompt's own start, useless for slicing the command text out.
      this._cmdPromptCol = kind === "B" ? buf.cursorX : -1;
    } else if (kind === "C") {
      if (this._cmdPromptLine >= 0) {
        var text = this._cmdText(buf, this._cmdPromptLine, this._cmdPromptCol);
        if (text) this._fireCommand(text, undefined);
        this._cmdPending = this._cmdCommit(this._cmdPromptLine, null, "osc", undefined, text);
        this._cmdPromptLine = -1;
        this._cmdPromptCol = -1;
      }
    } else if (kind === "D") {
      var exit = parts.length > 1 && parts[1] !== "" ? parseInt(parts[1], 10) : null;
      if (exit !== null && isNaN(exit)) exit = null;
      if (this._cmdPending) {
        this._cmdPending.exit = exit;
        this._cmdPending = null;
        this._queuePaint();
        this._queuePanelRefresh();
      } else if (this._cmdPromptLine >= 0) {
        // Shell emits A/D but never C: this D still belongs to whatever was typed
        // at the last prompt (empty Enters get a mark too — indistinguishable).
        this._cmdCommit(this._cmdPromptLine, exit, "osc", undefined,
          this._cmdText(buf, this._cmdPromptLine, this._cmdPromptCol));
        this._cmdPromptLine = -1;
      }
      this._fireCommand("", undefined); // the command is over, whatever it was
    }
  };

  /** UAPI.15 context signals are auxiliary. The stock systemd Bash hook does not
   * send command text, so the Enter-gated probe still owns command discovery.
   * OSC 3008 adds a stable command ID and an exact result to that mark. */
  RulerAddon.prototype._onOsc3008 = function (data) {
    var parsed = this._parseOsc3008(data);
    if (!parsed || this._cmdOscSeen) return;

    if (parsed.action === "start") {
      if (parsed.type !== "command") return;
      var record = { probe: null, entry: null, ended: false, exit: null };
      this._osc3008Commands.set(parsed.id, record);
      while (this._osc3008Commands.size > 64) {
        this._osc3008Commands.delete(this._osc3008Commands.keys().next().value);
      }
      for (var i = 0; i < this._cmdEnterProbes.length; i++) {
        var probe = this._cmdEnterProbes[i];
        if (!probe.isDisposed && !probe._osc3008Id) {
          probe._osc3008Id = parsed.id;
          record.probe = probe;
          break;
        }
      }
      return;
    }

    var current = this._osc3008Commands.get(parsed.id);
    if (!current) return;
    current.ended = true;
    current.exit = parsed.status !== null ? parsed.status : (parsed.exit === "success" ? 0 : null);
    if (current.entry) {
      if (current.exit !== null) current.entry.exit = current.exit;
      this._osc3008Commands.delete(parsed.id);
      this._queuePaint();
      this._queuePanelRefresh();
    } else if (!current.probe || current.probe.isDisposed) {
      this._osc3008Commands.delete(parsed.id);
    }
  };

  RulerAddon.prototype._parseOsc3008 = function (data) {
    if (typeof data !== "string" || data.length === 0 || data.length > 4096 || /[\x00-\x1f\x7f]/.test(data))
      return null;
    var fields = data.split(";");
    var first = /^(start|end)=(.*)$/.exec(fields[0]);
    if (!first || first[2].length === 0 || first[2].length > 256) return null;
    var id = "";
    for (var idIndex = 0; idIndex < first[2].length; idIndex++) {
      var character = first[2][idIndex];
      if (character !== "\\") {
        if (character.charCodeAt(0) < 32 || character.charCodeAt(0) > 126) return null;
        id += character;
        continue;
      }
      var escape = first[2].slice(idIndex, idIndex + 4);
      if (escape === "\\x3b") id += ";";
      else if (escape === "\\x5c") id += "\\";
      else return null;
      idIndex += 3;
    }
    if (id.length === 0 || id.length > 64) return null;
    var result = { action: first[1], id: id, type: null, exit: null, status: null };
    for (var i = 1; i < fields.length; i++) {
      var separator = fields[i].indexOf("=");
      if (separator <= 0) continue;
      var key = fields[i].slice(0, separator);
      var value = fields[i].slice(separator + 1);
      if (key === "type" && /^(service|session|shell|command|vm|container|elevate|chpriv|subcontext|remote|boot|app)$/.test(value))
        result.type = value;
      else if (key === "exit" && /^(success|failure|crash|interrupt)$/.test(value))
        result.exit = value;
      else if (key === "status" && /^[0-9]{1,20}$/.test(value)) {
        var status = parseInt(value, 10);
        if (status <= 255) result.status = status;
      }
    }
    return result;
  };

  /** First logical line of the buffer starting at (row, col), following soft wraps —
   * the command text for the running-command title. col -1 means "unknown" (no 133;B):
   * fall back to the prompt regex. Capped: a title needs a name, not the whole paste. */
  RulerAddon.prototype._cmdText = function (buf, row, col) {
    var line = buf.getLine(row);
    if (!line) return "";
    var full = line.translateToString(true);
    if (col < 0) {
      var m = CMD_SPLIT_RE.exec(full);
      if (!m || !m[2]) return "";
      col = full.length - m[2].length;
    }
    var text = full.slice(col);
    for (var r = row + 1; text.length < 256; r++) {
      var next = buf.getLine(r);
      if (!next || !next.isWrapped) break;
      text += next.translateToString(true);
    }
    return text.trim().slice(0, 256);
  };

  /** Hands a command start (or "" = end) to the page without letting a host-side
   * hook error break mark bookkeeping. */
  RulerAddon.prototype._fireCommand = function (text, epoch) {
    if (!this.onRunningCommand) return;
    try {
      this.onRunningCommand(text, epoch);
    } catch (err) {
      if (window.__pageTrace) window.__pageTrace("ruler onRunningCommand: " + (err && err.message));
    }
  };

  /** Native host hint from a saved icon or SSH version banner. It only permits prompt
   * interpretation; screen evidence still owns the displayed context. */
  RulerAddon.prototype.setPromptPlatform = function (platform) {
    if (platform === "cisco" || platform === "juniper" || platform === "nokia") {
      this._promptPlatform = platform;
      this._reportPromptContext(); // the initial prompt can arrive before the SSH banner hint
    }
  };

  /** Reports the best current-location label from a completed known prompt. The absolute
   * cursor line is part of the signature, so returning to the same context after a command
   * still reports that the command ended. */
  RulerAddon.prototype._reportPromptContext = function (force) {
    if (!this.onPromptContext || !this._term) return;
    var buf = this._term.buffer.active;
    if (!buf || buf.type === "alternate") return;
    var row = buf.baseY + buf.cursorY;
    var start = row;
    var line = buf.getLine(start);
    while (line && line.isWrapped && start > 0) {
      start--;
      line = buf.getLine(start);
    }
    if (!line) return;
    var promptText = line.translateToString(true);
    for (var nextRow = start + 1; nextRow <= row && promptText.length < 512; nextRow++) {
      var next = buf.getLine(nextRow);
      if (!next || !next.isWrapped) break;
      promptText += next.translateToString(true);
    }
    var label = null;
    var platform = null;
    var match = WINDOWS_IDLE_PROMPT_RE.exec(promptText);
    if (match) {
      label = match[1];
    } else if ((match = UNIX_BRACKETED_IDLE_PROMPT_RE.exec(promptText))) {
      label = match[1].trim();
    } else if ((match = UNIX_SPACED_IDLE_PROMPT_RE.exec(promptText))) {
      label = match[1].trim();
    } else if (NOKIA_MD_CLI_PROMPT_RE.test(promptText) && start > 0) {
      var contextLine = buf.getLine(start - 1);
      var contextText = contextLine && !contextLine.isWrapped
        ? contextLine.translateToString(true) : "";
      var context = NOKIA_MD_CLI_CONTEXT_RE.exec(contextText);
      if (context) {
        var modeKey = context[4] || context[2] || "";
        var path = context[3];
        if (context[4]) path = path.slice(context[4].length + 1);
        label = path || "MD-CLI";
        if (modeKey) {
          label = NOKIA_MD_CLI_MODES[modeKey] + (context[1] ? "*" : "")
            + " \u00b7 " + label;
        }
        platform = "nokia";
        this._promptPlatform = platform;
      }
    } else {
      var junosPrompt = JUNOS_PROMPT_RE.exec(promptText);
      if (junosPrompt) {
        var previousLine = start > 0 ? buf.getLine(start - 1) : null;
        var previousText = previousLine && !previousLine.isWrapped
          ? previousLine.translateToString(true) : "";
        var editContext = JUNOS_EDIT_CONTEXT_RE.exec(previousText);
        if (junosPrompt[1] === "#" && editContext) {
          var hierarchy = editContext[2] ? "/" + editContext[2] : "/";
          label = (editContext[1] ? editContext[1] + " \u00b7 " : "")
            + "configure \u00b7 " + hierarchy;
          platform = "juniper";
          this._promptPlatform = platform;
        } else if (junosPrompt[1] === ">") {
          var role = JUNOS_ROLE_RE.exec(previousText);
          var junosKnown = this._promptPlatform === "juniper" || !!role;
          if (!junosKnown) junosKnown = recentLineMatches(buf, start, JUNOS_BANNER_RE, 40);
          if (junosKnown) {
            label = (role ? role[1] + " \u00b7 " : "") + "operational";
            platform = "juniper";
            this._promptPlatform = platform;
          }
        }
      }
      if (!label) {
        var xrPrompt = CISCO_XR_PROMPT_RE.exec(promptText);
        var iosPrompt = xrPrompt ? null : CISCO_IOS_PROMPT_RE.exec(promptText);
        if (xrPrompt || iosPrompt) {
          var isXr = !!xrPrompt;
          var location = isXr ? xrPrompt[1] : "";
          var mode = isXr ? xrPrompt[3] : iosPrompt[2];
          var terminator = isXr ? xrPrompt[4] : iosPrompt[3];
          var ciscoKnown = isXr || this._promptPlatform === "cisco"
            || recentLineMatches(buf, start, CISCO_BANNER_RE, 60);
          // A mode suffix is safe shared network-CLI context. Bare EXEC prompts need
          // Cisco evidence so "server#" remains an ordinary Unix root prompt.
          if (mode || ciscoKnown) {
            var modeParts = ciscoModeParts(mode, terminator, isXr);
            if (location) modeParts.unshift(location);
            label = modeParts.join(" \u00b7 ");
            if (ciscoKnown) {
              platform = "cisco";
              this._promptPlatform = platform;
            }
          }
        }
      }
    }
    if (!label) return;
    var signature = row + ":" + platform + ":" + label;
    if (!force && signature === this._lastPromptSignature) return;
    this._lastPromptSignature = signature;
    try {
      this.onPromptContext(label, platform);
    } catch (err) {
      if (window.__pageTrace) window.__pageTrace("ruler onPromptContext: " + (err && err.message));
    }
  };

  /** Discovered marks: called by the page when the user's input contains Enter.
   * The cursor row is anchored with a probe marker and its text evaluated only after
   * the echo round trip settles — typed characters are echoed by the REMOTE side, so
   * a fast paste (or a laggy link) can put Enter ahead of its own command's echo.
   * The probe walks back across soft wraps to the row the prompt started on; a line
   * that never grows a prompt+command shape just disposes quietly. The page's title
   * epoch rides along so it can drop a discovered command whose prompt title already
   * moved on (a fast command finished before the probe fired). */
  RulerAddon.prototype.notifyEnter = function (epoch) {
    if (this._cmdOscSeen || this._term.buffer.active.type === "alternate") return;
    this._lastPromptSignature = null;
    var marker = this._term.registerMarker(0);
    if (!marker) return;
    var self = this;
    this._cmdEnterProbes.push(marker);
    var attempts = 0;
    var reported = false;
    function evaluate() {
      if (marker.isDisposed) { self._finishCommandProbe(marker); return; }
      if (self._cmdOscSeen || !self._term) {
        marker.dispose();
        self._finishCommandProbe(marker);
        return;
      }
      attempts++;
      // Probe the NORMAL buffer: the marker lives there, and a full-screen app may
      // have taken the alternate screen before the probe fired — that app IS the
      // running command, so the title must still come out. The mark stays gated on
      // the normal screen being active (_cmdCommit's math is cursor-relative).
      var norm = self._term.buffer.normal;
      var row = marker.line;
      var bufLine = norm.getLine(row);
      while (bufLine && bufLine.isWrapped && row > 0) {
        row--;
        bufLine = norm.getLine(row);
      }
      var lineText = bufLine ? bufLine.translateToString(true) : "";
      // Gate on CMD_PROMPT_RE (prompt plus a real command); CMD_SPLIT_RE then says
      // where the command starts, so the title slices at the column the mark used.
      var m = CMD_PROMPT_RE.test(lineText) ? CMD_SPLIT_RE.exec(lineText) : null;
      if (m) {
        var commandText = self._cmdText(norm, row, lineText.length - m[2].length);
        if (!reported) {
          reported = true;
          self._fireCommand(commandText, epoch);
        }
        if (self._term.buffer.active.type !== "alternate") {
          self._cmdCommit(row, null, "guess", marker._osc3008Id, commandText);
          marker.dispose();
          self._finishCommandProbe(marker);
          return;
        }
      }
      if (attempts < 2) setTimeout(evaluate, CMD_ECHO_RETRY_MS);
      else {
        marker.dispose();
        self._finishCommandProbe(marker);
      }
    }
    setTimeout(evaluate, CMD_ECHO_SETTLE_MS);
  };

  RulerAddon.prototype._finishCommandProbe = function (marker) {
    var index = this._cmdEnterProbes.indexOf(marker);
    if (index >= 0) this._cmdEnterProbes.splice(index, 1);
    if (!marker._osc3008Id) return;
    var record = this._osc3008Commands.get(marker._osc3008Id);
    if (!record) return;
    record.probe = null;
    if (record.ended && !record.entry) this._osc3008Commands.delete(marker._osc3008Id);
  };

  /** Adds a command mark at an absolute line (idempotent per line; an exit code
   * updates an existing mark in place). Markers keep marks trim-safe for free.
   * The command text rides along for the panel and popover — the line's prompt can
   * be re-parsed later, but only the OSC 133;B column knows where a fancy
   * shell-integration prompt ends. */
  RulerAddon.prototype._cmdCommit = function (line, exit, src, contextId, text) {
    for (var i = 0; i < this._cmdMarks.length; i++) {
      if (this._cmdMarks[i].marker.line === line) {
        if (exit !== null) {
          this._cmdMarks[i].exit = exit;
          this._queuePaint();
        }
        if (text && !this._cmdMarks[i].text) this._cmdMarks[i].text = text;
        this._associateOsc3008(contextId, this._cmdMarks[i]);
        this._queuePanelRefresh();
        return this._cmdMarks[i];
      }
    }
    var buf = this._term.buffer.active;
    var marker = this._term.registerMarker(line - (buf.baseY + buf.cursorY));
    if (!marker) return null;
    var self = this;
    var entry = { marker: marker, exit: exit, src: src, text: text || "" };
    marker.onDispose(function () {
      var idx = self._cmdMarks.indexOf(entry);
      if (idx >= 0) self._cmdMarks.splice(idx, 1);
      if (self._cmdPending === entry) self._cmdPending = null;
      self._queuePaint();
      self._queuePanelRefresh();
    });
    this._cmdMarks.push(entry);
    this._associateOsc3008(contextId, entry);
    this._notifyCommand(line);
    this._queuePaint();
    this._queuePanelRefresh();
    return entry;
  };

  RulerAddon.prototype._associateOsc3008 = function (contextId, entry) {
    if (!contextId) return;
    var record = this._osc3008Commands.get(contextId);
    if (!record) return;
    record.entry = entry;
    record.probe = null;
    if (!record.ended) return;
    if (record.exit !== null) entry.exit = record.exit;
    this._osc3008Commands.delete(contextId);
  };

  /** Register a listener for commands as they are marked (Phase 6.2 agent detection).
   * The ruler already owns both discovery paths — OSC 133 and the Enter-gated probe —
   * so agent identity rides the same evidence instead of re-deriving it. Distinct from
   * the onRunningCommand property: this fires per MARK, never reports an end, and is
   * not epoch-gated. */
  RulerAddon.prototype.onCommandMark = function (callback) {
    this._cmdObserver = callback;
  };

  /** Hand the listener just the command part of a freshly marked prompt line. */
  RulerAddon.prototype._notifyCommand = function (line) {
    if (!this._cmdObserver) return;
    var bufLine = this._term.buffer.active.getLine(line);
    if (!bufLine) return;
    var text = bufLine.translateToString(true).trim();
    var match = text.match(CMD_SPLIT_RE);
    var command = match ? match[2] : text;
    if (!command) return;
    try {
      this._cmdObserver(command);
    } catch (err) {
      if (window.__pageTrace) window.__pageTrace("ruler onCommandMark: " + (err && err.message));
    }
  };

  /** Scroll to the previous (dir<0) or next (dir>0) command mark relative to the
   * viewport center, flashing it. Returns whether a mark was found. */
  RulerAddon.prototype.jumpCommand = function (dir) {
    var term = this._term;
    var buf = term.buffer.active;
    if (buf.type === "alternate" || this._cmdMarks.length === 0) return false;
    var center = buf.viewportY + Math.floor(term.rows / 2);
    var best = -1;
    for (var i = 0; i < this._cmdMarks.length; i++) {
      var l = this._cmdMarks[i].marker.line;
      if (dir > 0 ? l > center : l < center) {
        if (best < 0 || (dir > 0 ? l < best : l > best)) best = l;
      }
    }
    if (best < 0) return false;
    this._scrollLineToCenter(best);
    this._flashLine(best);
    return true;
  };

  // ---- commands panel + popover actions ----

  /** One command mark as plain data. Stored text wins; otherwise the mark's line is
   * re-parsed, falling back to the raw line for prompts the regex does not know. */
  RulerAddon.prototype._commandInfo = function (entry) {
    var buf = this._term.buffer.normal || this._term.buffer.active;
    var line = entry.marker.line;
    var text = entry.text || this._cmdText(buf, line, -1);
    if (!text) {
      var bufLine = buf.getLine(line);
      text = bufLine ? bufLine.translateToString(true).trim().slice(0, 256) : "";
    }
    return {
      line: line, exit: entry.exit, src: entry.src, text: text,
      unixMs: this._timeForLine(line)
    };
  };

  /** All command marks, ascending by buffer line: { line, exit, src, text, unixMs }. */
  RulerAddon.prototype.getCommands = function () {
    if (!this._term) return [];
    var marks = this._cmdMarks.slice().sort(function (a, b) {
      return a.marker.line - b.marker.line;
    });
    var result = [];
    for (var i = 0; i < marks.length; i++) result.push(this._commandInfo(marks[i]));
    return result;
  };

  /** The output of the command marked at an absolute line, led by the command's own
   * prompt line so a paste reads like a transcript: buffer text between the command's
   * logical line and the next command mark, soft wraps joined. The live idle prompt
   * (the cursor's logical line) is excluded, and trailing blank lines plus
   * empty-Enter prompt lines are dropped — the latter recognized by equality with
   * this or the next mark's prompt, never by shape alone, because output such as
   * "</html>" parses as a bare prompt shape. A command with no surviving output
   * returns "" (the copy buttons report "No output" instead of a lone header). */
  RulerAddon.prototype.getCommandOutput = function (line) {
    var term = this._term;
    if (!term) return "";
    var buf = term.buffer.normal || term.buffer.active;

    function promptAt(row) {
      var promptLine = buf.getLine(row);
      if (!promptLine) return null;
      var m = CMD_SPLIT_RE.exec(promptLine.translateToString(true));
      return m ? m[1] : null;
    }

    var start = line + 1;
    var startLine;
    while ((startLine = buf.getLine(start)) && startLine.isWrapped) start++;

    var end = buf.length;
    var nextMarkLine = -1;
    for (var i = 0; i < this._cmdMarks.length; i++) {
      var markLine = this._cmdMarks[i].marker.line;
      if (markLine > line && markLine < end) { end = markLine; nextMarkLine = markLine; }
    }
    var cursor = buf.baseY + buf.cursorY;
    var cursorLine = buf.getLine(cursor);
    while (cursorLine && cursorLine.isWrapped && cursor > 0) {
      cursor--;
      cursorLine = buf.getLine(cursor);
    }
    if (cursor > line && cursor < end) end = cursor;

    var out = [];
    for (var r = start; r < end; r++) {
      var bufLine = buf.getLine(r);
      if (!bufLine) continue;
      var text = bufLine.translateToString(true);
      if (bufLine.isWrapped && out.length > 0) out[out.length - 1] += text;
      else out.push(text);
    }
    var ownPrompt = promptAt(line);
    var nextPrompt = nextMarkLine >= 0 ? promptAt(nextMarkLine) : null;
    while (out.length > 0) {
      var last = out[out.length - 1];
      if (last !== "" && last !== ownPrompt && last !== nextPrompt) break;
      out.pop();
    }
    if (out.length === 0) return "";

    // Lead with the command's own line; rows line+1..start-1 are its soft wraps.
    var head = buf.getLine(line);
    var headText = head ? head.translateToString(true) : "";
    for (var w = line + 1; w < start; w++) {
      var wrapRow = buf.getLine(w);
      if (wrapRow) headText += wrapRow.translateToString(true);
    }
    if (headText) out.unshift(headText);
    return out.join("\n");
  };

  /** Center and flash a command's line (panel rows and the tooltip's Jump to). */
  RulerAddon.prototype.jumpToCommand = function (line) {
    if (!this._term || this._term.buffer.active.type === "alternate") return false;
    this._scrollLineToCenter(line);
    this._flashLine(line);
    return true;
  };

  RulerAddon.prototype._copyCommandOutput = function (line, button, doneLabel, emptyLabel) {
    var output = this.getCommandOutput(line);
    // Never clobber the clipboard with nothing; say so on the button instead.
    if (output && typeof navigator !== "undefined" && navigator.clipboard) {
      navigator.clipboard.writeText(output).catch(function () {});
    }
    if (!button) return;
    var restore = button.textContent;
    button.textContent = output ? doneLabel : emptyLabel;
    setTimeout(function () {
      // The panel rebuilds rows on refresh; a stale button just fades away.
      button.textContent = restore;
    }, 1200);
  };

  /** Open/close the commands panel ("show commands"). Boolean forces a state.
   * Called by the page for Ctrl+Shift+O and the host's "toggleCommands" message.
   * Every state change (this includes the panel's own ✕) reports back through
   * onCommandsPanelChanged so the host's toggle button can stay truthful. */
  RulerAddon.prototype.toggleCommandsPanel = function (open) {
    var next = typeof open === "boolean" ? open : !this._cmdPanelOpen;
    this._cmdPanelOpen = next;
    if (this.onCommandsPanelChanged) {
      try {
        this.onCommandsPanelChanged(next);
      } catch (err) {
        if (window.__pageTrace) window.__pageTrace("ruler onCommandsPanelChanged: " + (err && err.message));
      }
    }
    if (!this._cmdPanel) return;
    this._cmdPanel.style.display = next ? "flex" : "none";
    if (next) {
      this._refreshCommandsPanel();
      if (this._cmdPanelList) this._cmdPanelList.scrollTop = this._cmdPanelList.scrollHeight;
    }
  };

  /** Coalesced refresh, a no-op while the panel is closed. Every mark mutation site
   * calls this — commit, exit updates from 133;D / OSC 3008, and trim disposal. */
  RulerAddon.prototype._queuePanelRefresh = function () {
    if (!this._cmdPanelOpen || this._cmdPanelRefreshQueued) return;
    var self = this;
    this._cmdPanelRefreshQueued = true;
    requestAnimationFrame(function () {
      self._cmdPanelRefreshQueued = false;
      if (!self._cmdPanelOpen) return;
      try {
        self._refreshCommandsPanel();
      } catch (err) {
        if (window.__pageTrace) window.__pageTrace("ruler panel: " + (err && err.message));
      }
    });
  };

  /** Rebuild the panel rows from the live mark set. DOM via textContent only —
   * command text is untrusted terminal output. Rows close over the ENTRY, not its
   * line number: markers shift with scrollback trimming, so the line is read fresh
   * on click. */
  RulerAddon.prototype._refreshCommandsPanel = function () {
    var list = this._cmdPanelList;
    if (!list || !list.ownerDocument || !this._term) return;
    var doc = list.ownerDocument;
    var self = this;
    var marks = this._cmdMarks.slice().sort(function (a, b) {
      return a.marker.line - b.marker.line;
    });
    if (this._cmdPanelCount)
      this._cmdPanelCount.textContent = marks.length === 0 ? "" : String(marks.length);
    var stick = list.scrollHeight - list.scrollTop - list.clientHeight < 4;
    list.textContent = "";
    if (marks.length === 0) {
      var empty = doc.createElement("div");
      empty.className = "srp-empty";
      empty.textContent = "No commands in this session yet.";
      list.appendChild(empty);
      return;
    }
    var c = this._colors;
    for (var i = 0; i < marks.length; i++) {
      (function (entry) {
        var info = self._commandInfo(entry);
        var row = doc.createElement("div");
        row.className = "srp-row";
        var dot = doc.createElement("span");
        dot.className = "srp-dot";
        dot.style.background =
          info.exit === null ? c.cmdUnknown : (info.exit === 0 ? c.cmdOk : c.cmdFail);
        if (info.exit !== null) dot.title = "exit " + info.exit;
        var text = doc.createElement("span");
        text.className = "srp-cmd";
        text.textContent = info.text || "(command)";
        text.title = info.text || "";
        var time = doc.createElement("span");
        time.className = "srp-time";
        var stamp = self._formatTimestamp(info.unixMs);
        if (stamp) {
          time.textContent = stamp.clock;
          time.title = stamp.clock + " · " + stamp.relative;
        }
        var copy = doc.createElement("button");
        copy.className = "srp-copy";
        copy.title = "Copy output";
        copy.textContent = "⧉";
        row.appendChild(dot);
        row.appendChild(text);
        row.appendChild(time);
        row.appendChild(copy);
        if (row.addEventListener) {
          row.addEventListener("click", function () {
            if (entry.marker.isDisposed) return;
            self.jumpToCommand(entry.marker.line);
            self._term.focus();
          });
          copy.addEventListener("click", function (e) {
            if (e && e.stopPropagation) e.stopPropagation();
            if (entry.marker.isDisposed) return;
            self._copyCommandOutput(entry.marker.line, copy, "✓", "∅");
          });
        }
        list.appendChild(row);
      })(marks[i]);
    }
    if (stick) list.scrollTop = list.scrollHeight;
  };

  // ---- highlight index (Phase 9.3 content lane) ----

  /** Called by the page whenever the session's active highlight rule set changes.
   * Only rules flagged showInOverview are indexed. Replacing the set invalidates
   * the whole index (a toggle changes what every line's bitmask means). */
  RulerAddon.prototype.setHighlightRules = function (rules) {
    var compiled = [];
    for (var i = 0; i < (rules || []).length && compiled.length < HL_MAX_RULES; i++) {
      var r = rules[i];
      if (!r || !r.showInOverview || !r.pattern) continue;
      try {
        compiled.push({
          id: r.id,
          name: r.name || r.id,
          re: new RegExp(r.pattern, r.matchCase ? "" : "i"),
          color: r.color || "#888888"
        });
      } catch (err) {
        // Invalid in this engine — the highlight addon skipped it too.
      }
    }
    this._hlRules = compiled;
    this._hlRebuild();
  };

  RulerAddon.prototype._hlRebuild = function () {
    this._hlIndex.clear();
    this._hlFrontier = 0;
    this._hlPruneAt = 0;
    if (this._hlAnchor) {
      var anchor = this._hlAnchor;
      this._hlAnchor = null; // deliberate disposal — don't let onDispose re-rebuild
      anchor.marker.dispose();
    }
    this._hlKick();
    this._queuePaint();
  };

  /** Schedule an indexer pass iff there is (potential) work: rules active, normal
   * buffer, and unscanned lines above the cursor row. Called from hot paths
   * (onLineFeed, onRender), so it must stay cheap. */
  RulerAddon.prototype._hlKick = function () {
    if (this._hlRules.length === 0 || this._hlScanScheduled || !this._term) return;
    var buf = this._term.buffer.active;
    if (buf.type === "alternate") return;
    if (this._hlAnchor &&
        this._hlFrontier >= this._hlVirt(buf.baseY + buf.cursorY)) return;
    var self = this;
    this._hlScanScheduled = true;
    var run = function (deadline) {
      self._hlScanScheduled = false;
      if (!self._term) return;
      try {
        self._hlScan(deadline);
      } catch (err) {
        if (window.__pageTrace) window.__pageTrace("ruler index: " + (err && err.message));
      }
    };
    // Budgeted idle-time work; the timeout keeps backfill moving under load.
    if (window.requestIdleCallback) window.requestIdleCallback(run, { timeout: 500 });
    else requestAnimationFrame(function () { run(null); });
  };

  RulerAddon.prototype._hlVirt = function (abs) {
    return abs + this._hlAnchor.virtual - this._hlAnchor.marker.line;
  };

  /** The sentinel marker anchoring virtual line numbers. Re-anchored to the cursor
   * once it lags far behind, so scrollback trimming can't reach it between passes;
   * if a flood trims it anyway, onDispose falls back to a full rebuild. */
  RulerAddon.prototype._hlEnsureAnchor = function () {
    var term = this._term;
    var buf = term.buffer.active;
    if (this._hlAnchor) {
      if (buf.baseY + buf.cursorY - this._hlAnchor.marker.line > HL_REANCHOR_GAP) {
        var next = term.registerMarker(0);
        if (next) {
          var virt = this._hlVirt(next.line);
          var old = this._hlAnchor;
          this._hlAnchor = { marker: next, virtual: virt };
          this._hlWatchAnchor(next);
          old.marker.dispose();
        }
      }
      return this._hlAnchor;
    }
    var marker = term.registerMarker(0);
    if (!marker) return null;
    // Fresh anchor after a rebuild: virtual == absolute at this instant.
    this._hlAnchor = { marker: marker, virtual: marker.line };
    this._hlWatchAnchor(marker);
    return this._hlAnchor;
  };

  RulerAddon.prototype._hlWatchAnchor = function (marker) {
    var self = this;
    marker.onDispose(function () {
      if (self._hlAnchor && self._hlAnchor.marker === marker) {
        self._hlAnchor = null;
        self._hlRebuild();
      }
    });
  };

  /** One budgeted indexer pass: scan completed lines (everything above the cursor
   * row) from the frontier forward, then reschedule itself if lines remain. */
  RulerAddon.prototype._hlScan = function (deadline) {
    var buf = this._term.buffer.active;
    if (this._hlRules.length === 0 || buf.type === "alternate") return;
    var anchor = this._hlEnsureAnchor();
    if (!anchor) return;
    var offset = anchor.virtual - anchor.marker.line; // virtual = absolute + offset

    // Drop entries for trimmed-away lines once enough have accumulated.
    var topVirt = offset; // == this._hlVirt(0)
    if (topVirt > this._hlPruneAt + HL_REANCHOR_GAP) {
      this._hlPruneAt = topVirt;
      var dead = [];
      this._hlIndex.forEach(function (mask, virt) { if (virt < topVirt) dead.push(virt); });
      for (var d = 0; d < dead.length; d++) this._hlIndex.delete(dead[d]);
    }
    if (this._hlFrontier < topVirt) this._hlFrontier = topVirt; // trimmed before scanned

    var bottomVirt = buf.baseY + buf.cursorY + offset; // cursor row is still being written
    var rules = this._hlRules;
    var scanned = 0;
    var changed = false;
    while (this._hlFrontier < bottomVirt) {
      if (deadline ? deadline.timeRemaining() < HL_IDLE_MIN_MS : scanned >= HL_SLICE) break;
      var bufLine = buf.getLine(this._hlFrontier - offset);
      var virt = this._hlFrontier++;
      scanned++;
      if (!bufLine) continue;
      var text = bufLine.translateToString(true);
      if (!text) continue;
      var mask = 0;
      for (var r = 0; r < rules.length; r++) {
        if (rules[r].re.test(text)) mask |= (1 << r);
      }
      if (mask !== 0) {
        this._hlIndex.set(virt, mask);
        changed = true;
      }
    }
    if (this._hlFrontier < bottomVirt) {
      this._queuePaint(); // catching-up veil recedes even when nothing matched
      this._hlKick();
    } else if (changed) {
      this._queuePaint();
    }
  };

  // ---- interaction ----

  RulerAddon.prototype._totalLines = function () {
    return this._term.buffer.active.length;
  };

  RulerAddon.prototype._lineAtY = function (y) {
    var h = this._strip.clientHeight;
    if (h <= 0) return 0;
    return Math.round((y / h) * this._totalLines());
  };

  RulerAddon.prototype._scrollLineToCenter = function (line) {
    var term = this._term;
    var top = Math.round(line - term.rows / 2);
    var max = this._totalLines() - term.rows;
    term.scrollToLine(Math.max(0, Math.min(top, Math.max(max, 0))));
  };

  RulerAddon.prototype._onPointerDown = function (e) {
    if (e.button !== 0) return;
    e.preventDefault(); // keep focus in the terminal
    this._hideTooltip();
    this._drag = { pointerId: e.pointerId, startY: e.offsetY, moved: false };
    this._syncThumbVisibility();
    try { this._strip.setPointerCapture(e.pointerId); } catch (err) {}
  };

  RulerAddon.prototype._onPointerMove = function (e) {
    if (this._drag) {
      if (e.pointerId !== this._drag.pointerId) return;
      // Recover even if WebView2 missed pointerup, pointercancel, and window blur.
      if (typeof e.buttons === "number" && (e.buttons & 1) === 0) {
        this._cancelDrag(e.pointerId);
        return;
      }
      if (!this._drag.moved && Math.abs(e.offsetY - this._drag.startY) < DRAG_THRESHOLD) return;
      this._drag.moved = true;
      this._scrollLineToCenter(this._lineAtY(e.offsetY));
      return;
    }
    this._scheduleTooltip(e.offsetY);
  };

  RulerAddon.prototype._onPointerUp = function (e) {
    if (!this._drag || e.pointerId !== this._drag.pointerId) return;
    var wasClick = !this._drag.moved;
    this._cancelDrag(e.pointerId);
    if (!wasClick) return;

    var line = this._lineAtY(e.offsetY);
    var snapped = this._snapToMark(e.offsetY);
    if (snapped >= 0) line = snapped;
    this._scrollLineToCenter(line);
    this._flashLine(line);
    this._term.focus();
  };

  RulerAddon.prototype._cancelDrag = function (pointerId, releaseCapture) {
    if (!this._drag) return;
    if (typeof pointerId === "number" && pointerId !== this._drag.pointerId) return;
    var capturedPointerId = this._drag.pointerId;
    this._drag = null;
    this._syncThumbVisibility();
    if (releaseCapture === false || !this._strip) return;
    try { this._strip.releasePointerCapture(capturedPointerId); } catch (err) {}
  };

  /** Nearest bookmark, match, or highlight-hit line within SNAP_PX of the pointer, or -1. */
  RulerAddon.prototype._snapToMark = function (y) {
    var h = this._strip.clientHeight;
    var total = this._totalLines();
    if (h <= 0 || total <= 0) return -1;
    var best = -1, bestDist = SNAP_PX + 1;

    function consider(line) {
      if (line < 0 || line >= total) return;
      var dist = Math.abs((line / total) * h - y);
      if (dist < bestDist) { bestDist = dist; best = line; }
    }
    for (var i = 0; i < this._matchLines.length; i++) consider(this._matchLines[i]);
    for (var b = 0; b < this._bookmarks.length; b++) consider(this._bookmarks[b].marker.line);
    for (var m = 0; m < this._cmdMarks.length; m++) consider(this._cmdMarks[m].marker.line);
    if (this._hlAnchor) {
      var offset = this._hlAnchor.virtual - this._hlAnchor.marker.line;
      this._hlIndex.forEach(function (mask, virt) { consider(virt - offset); });
    }
    return best;
  };

  RulerAddon.prototype._flashLine = function (line) {
    this._clearFlash();
    var term = this._term;
    var buf = term.buffer.active;
    var marker = term.registerMarker(line - (buf.baseY + buf.cursorY));
    if (!marker) return;
    var deco = term.registerDecoration({ marker: marker, x: 0, width: term.cols, layer: "top" });
    if (!deco) { marker.dispose(); return; }
    var flashColor = this._colors.flash;
    deco.onRender(function (element) {
      element.style.backgroundColor = flashColor;
      element.style.transition = "background-color 0.4s ease-out";
    });
    var self = this;
    this._flash = {
      deco: deco, marker: marker,
      timer: setTimeout(function () { self._clearFlash(); }, FLASH_MS)
    };
  };

  RulerAddon.prototype._clearFlash = function () {
    if (!this._flash) return;
    clearTimeout(this._flash.timer);
    this._flash.deco.dispose();
    this._flash.marker.dispose();
    this._flash = null;
  };

  // ---- tooltip ----

  RulerAddon.prototype._scheduleTooltip = function (y) {
    var self = this;
    if (this._hoverTimer) clearTimeout(this._hoverTimer);
    this._hoverTimer = setTimeout(function () {
      self._hoverTimer = null;
      try {
        self._showTooltip(y);
      } catch (err) {
        if (window.__pageTrace) window.__pageTrace("ruler tooltip: " + (err && err.message));
      }
    }, HOVER_DELAY);
  };

  RulerAddon.prototype._showTooltip = function (y) {
    var term = this._term;
    var buf = term.buffer.active;
    if (!term || buf.type === "alternate") return;
    var h = this._strip.clientHeight;
    var total = this._totalLines();
    if (h <= 0 || total <= 0) return;

    var line = this._lineAtY(y);
    var lineRadius = Math.max(1, Math.ceil((SNAP_PX / h) * total));
    var lo = line - lineRadius, hi = line + lineRadius;

    var matches = 0, firstMatch = -1;
    for (var i = 0; i < this._matchLines.length; i++) {
      var m = this._matchLines[i];
      if (m >= lo && m <= hi) { matches++; if (firstMatch < 0) firstMatch = m; }
    }
    var bookmarks = 0;
    for (var b = 0; b < this._bookmarks.length; b++) {
      var bl = this._bookmarks[b].marker.line;
      if (bl >= lo && bl <= hi) bookmarks++;
    }

    // Per-rule highlight-hit counts within the hover region, e.g. "3× Down/error states".
    var hlRules = this._hlRules;
    var hlCounts = [];
    var firstHl = -1;
    if (this._hlAnchor && this._hlIndex.size > 0) {
      var offset = this._hlAnchor.virtual - this._hlAnchor.marker.line;
      this._hlIndex.forEach(function (mask, virt) {
        var l = virt - offset;
        if (l < lo || l > hi) return;
        if (firstHl < 0 || l < firstHl) firstHl = l;
        for (var r = 0; r < hlRules.length; r++) {
          if (mask & (1 << r)) hlCounts[r] = (hlCounts[r] || 0) + 1;
        }
      });
    }

    // The action buttons (and the sample fallback) target the mark NEAREST to the
    // pointer — with several commands in the region, "Jump to" must not surprise.
    var commands = 0, nearestCommand = null, nearestDist = Infinity;
    for (var cm = 0; cm < this._cmdMarks.length; cm++) {
      var cl = this._cmdMarks[cm].marker.line;
      if (cl >= lo && cl <= hi) {
        commands++;
        var commandDist = Math.abs(cl - line);
        if (commandDist < nearestDist) {
          nearestDist = commandDist;
          nearestCommand = this._cmdMarks[cm];
        }
      }
    }

    var sampleLine = firstMatch >= 0 ? firstMatch
      : firstHl >= 0 ? firstHl
      : nearestCommand ? nearestCommand.marker.line
      : line;

    var metadata = [];
    function addMetadata(text, color) { metadata.push({ text: text, color: color || null }); }
    if (commands === 1) {
      addMetadata(nearestCommand.exit === null ? "command" : "exit " + nearestCommand.exit);
    } else if (commands > 1) {
      addMetadata(commands + " commands");
    }
    var timestamp = this._formatTimestamp(this._timeForLine(sampleLine));
    if (timestamp) {
      addMetadata(timestamp.clock);
      addMetadata(timestamp.relative);
    }
    if (matches > 0)
      addMetadata(matches === 1 ? "1 match" : matches + " matches", this._colors.activeMatch);
    if (bookmarks > 0)
      addMetadata(bookmarks === 1 ? "1 bookmark" : bookmarks + " bookmarks", this._colors.bookmark);
    for (var hr = 0; hr < hlRules.length; hr++) {
      if (hlCounts[hr]) addMetadata(hlCounts[hr] + "× " + hlRules[hr].name, hlRules[hr].color);
    }

    var bufLine = buf.getLine(Math.max(0, Math.min(sampleLine, total - 1)));
    var sample = bufLine ? bufLine.translateToString(true).trim() : "";
    sample = sample.length > 72 ? sample.slice(0, 72) + "…" : sample;
    if (!sample) sample = "line " + Math.min(line + 1, total);
    else if (commands === 0) metadata.unshift({ text: "line " + Math.min(line + 1, total), color: null });

    this._tooltipCommand = nearestCommand;
    var actions = null;
    if (nearestCommand) {
      var self = this;
      actions = [
        { label: "Jump to", run: function () {
            var target = self._tooltipCommand;
            if (!target || target.marker.isDisposed) return;
            self.jumpToCommand(target.marker.line);
            self._hideTooltip();
            self._term.focus();
          } },
        { label: "Copy output", run: function (button) {
            var target = self._tooltipCommand;
            if (!target || target.marker.isDisposed) return;
            self._copyCommandOutput(target.marker.line, button, "Copied", "No output");
          } }
      ];
    }

    var c = this._colors;
    var tip = this._tooltip;
    this._renderTooltip(tip, sample, metadata, actions);
    tip.style.background = c.tooltipBg;
    tip.style.color = c.tooltipFg;
    tip.style.border = "1px solid " + c.tooltipBorder;
    // Only a card with buttons is allowed to catch the pointer; see activate().
    tip.style.pointerEvents = actions ? "auto" : "none";
    tip.style.display = "block";
    var tipH = tip.offsetHeight;
    tip.style.top = Math.max(0, Math.min(y - tipH / 2, h - tipH)) + "px";
  };

  /** Render the tooltip as a compact command card: command/sample first, metadata below,
   * then any action buttons ("Jump to" / "Copy output" when a command mark is near).
   * Build DOM with textContent only; terminal output is untrusted. Plain-object test doubles
   * use the newline fallback (and are never interactive, so actions are dropped there). */
  RulerAddon.prototype._renderTooltip = function (tip, sample, metadata, actions) {
    var prompt = "", command = sample;
    var match = sample.match(CMD_SPLIT_RE);
    if (match) { prompt = match[1]; command = match[2]; }

    var metadataText = metadata.map(function (part) { return part.text; }).join(" · ");
    if (!tip.ownerDocument || typeof tip.appendChild !== "function") {
      tip.textContent = sample + (metadataText ? "\n" + metadataText : "");
      return;
    }

    tip.textContent = "";
    var doc = tip.ownerDocument;
    var top = doc.createElement("div");
    top.style.cssText = "font-weight:600;color:" + this._colors.tooltipFg +
      ";white-space:nowrap;overflow:hidden;text-overflow:ellipsis;";
    if (prompt) {
      var promptSpan = doc.createElement("span");
      promptSpan.style.color = this._colors.cmdOk;
      promptSpan.textContent = prompt;
      top.appendChild(promptSpan);
      if (command) top.appendChild(doc.createTextNode(" " + command));
    } else {
      top.textContent = sample;
    }
    tip.appendChild(top);

    if (metadata.length > 0) {
      var bottom = doc.createElement("div");
      bottom.style.cssText = "color:" + (this._colors.tooltipMuted || this._colors.tooltipFg) +
        ";white-space:nowrap;";
      for (var i = 0; i < metadata.length; i++) {
        if (i > 0) bottom.appendChild(doc.createTextNode(" · "));
        var span = doc.createElement("span");
        span.textContent = metadata[i].text;
        if (metadata[i].color) span.style.color = metadata[i].color;
        bottom.appendChild(span);
      }
      tip.appendChild(bottom);
    }

    if (actions && actions.length > 0) {
      var actionRow = doc.createElement("div");
      actionRow.className = "srt-actions";
      for (var a = 0; a < actions.length; a++) {
        (function (action) {
          var button = doc.createElement("button");
          button.className = "srt-btn";
          button.textContent = action.label;
          if (button.addEventListener) {
            button.addEventListener("click", function (e) {
              if (e && e.stopPropagation) e.stopPropagation();
              action.run(button);
            });
          }
          actionRow.appendChild(button);
        })(actions[a]);
      }
      tip.appendChild(actionRow);
    }
  };

  RulerAddon.prototype._hideTooltip = function () {
    this._cancelTooltipHide();
    if (this._hoverTimer) { clearTimeout(this._hoverTimer); this._hoverTimer = null; }
    this._tooltipCommand = null;
    if (this._tooltip) {
      this._tooltip.style.display = "none";
      this._tooltip.style.pointerEvents = "none";
    }
  };

  /** Delayed hide with a cancel, so leaving the strip toward the tooltip's action
   * buttons (a 6px gap) does not dismiss them mid-flight. */
  RulerAddon.prototype._scheduleTooltipHide = function () {
    var self = this;
    this._cancelTooltipHide();
    this._tooltipHideTimer = setTimeout(function () {
      self._tooltipHideTimer = null;
      self._hideTooltip();
    }, 250);
  };

  RulerAddon.prototype._cancelTooltipHide = function () {
    if (this._tooltipHideTimer) {
      clearTimeout(this._tooltipHideTimer);
      this._tooltipHideTimer = null;
    }
  };

  // ---- painting ----

  RulerAddon.prototype._syncThumbVisibility = function () {
    if (!this._thumb) return;
    var visible = this._isPointerOver || this._drag !== null;
    this._thumb.style.transitionDuration = (visible ? THUMB_SHOW_MS : THUMB_HIDE_MS) + "ms";
    this._thumb.style.opacity = visible ? "1" : "0";
  };

  RulerAddon.prototype._queuePaint = function () {
    var self = this;
    if (this._paintQueued) return;
    this._paintQueued = true;
    requestAnimationFrame(function () {
      self._paintQueued = false;
      try {
        self._paint();
      } catch (err) {
        if (window.__pageTrace) window.__pageTrace("ruler paint: " + (err && err.message));
      }
    });
  };

  RulerAddon.prototype._paint = function () {
    var term = this._term;
    if (!term || !this._strip) return;
    var buf = term.buffer.active;

    if (buf.type === "alternate") {
      this._strip.style.display = "none";
      if (this._cmdPanel) this._cmdPanel.style.display = "none";
      return;
    }
    this._strip.style.display = "block";
    if (this._cmdPanel) this._cmdPanel.style.display = this._cmdPanelOpen ? "flex" : "none";

    var cssW = WIDTH;
    var cssH = this._strip.clientHeight;
    if (cssH <= 0) return;
    var dpr = window.devicePixelRatio || 1;
    var canvas = this._canvas;
    var devW = Math.round(cssW * dpr), devH = Math.round(cssH * dpr);
    if (canvas.width !== devW || canvas.height !== devH) {
      canvas.width = devW;
      canvas.height = devH;
    }

    var ctx = canvas.getContext("2d");
    var c = this._colors;
    var total = Math.max(buf.length, 1);
    var calm = this._isSplit && !this._isPointerOver;
    var ordinaryAlpha = calm ? (this._isGroupFocused ? 0.42 : 0.25) : 1;
    var importantAlpha = calm ? (this._isGroupFocused ? 0.90 : 0.62) : 1;
    var searchAlpha = calm ? (this._isGroupFocused ? 0.75 : 0.50) : 1;
    var visualWidth = Math.round(WIDTH * dpr);
    var visualLeft = devW - visualWidth;

    this._strip.dataset.presentation = calm
      ? (this._isGroupFocused ? "calm-focused" : "calm-unfocused")
      : "full";

    ctx.clearRect(0, 0, devW, devH);
    ctx.fillStyle = c.background;
    ctx.fillRect(visualLeft, 0, visualWidth, devH);
    ctx.fillStyle = c.border;
    ctx.fillRect(visualLeft, 0, Math.max(1, Math.round(dpr)), devH);

    // Device-pixel row buckets: count per row, alpha encodes density. 3 CSS px per
    // tick: 2 px reads as noise on a tall strip (user feedback: "extremely subtle").
    var tickH = Math.max(3, Math.round(3 * dpr));

    function markerRow(line) {
      var row = Math.min(devH - tickH, Math.round((line / total) * devH));
      // Calm mode combines nearby marks into one short block instead of a noisy stack.
      return calm ? Math.floor(row / tickH) * tickH : row;
    }

    function bucketRows(lines) {
      var rows = new Map();
      for (var i = 0; i < lines.length; i++) {
        var row = markerRow(lines[i]);
        rows.set(row, (rows.get(row) || 0) + 1);
      }
      return rows;
    }

    var laneRx = visualLeft + Math.round(6 * dpr);
    var laneRw = devW - laneRx - Math.round(1 * dpr);

    // Right (content) lane underlay: highlight-rule hits in dimmed rule colors.
    // Search ticks paint over them, so an active search stays dominant.
    if (this._hlRules.length > 0 && this._hlAnchor) {
      var hlOffset = this._hlAnchor.virtual - this._hlAnchor.marker.line;
      var hlRules = this._hlRules;
      ctx.globalAlpha = HL_TICK_ALPHA * ordinaryAlpha;
      this._hlIndex.forEach(function (mask, virt) {
        var line = virt - hlOffset;
        if (line < 0 || line >= total) return;
        var row = markerRow(line);
        // Highest set bit: the last matching rule wins, as in the viewport decorations.
        ctx.fillStyle = hlRules[31 - Math.clz32(mask)].color;
        ctx.fillRect(laneRx, row, laneRw, tickH);
      });
      ctx.globalAlpha = 1;

      // Catching-up veil over the span the background indexer hasn't reached yet.
      var frontierLine = this._hlFrontier - hlOffset;
      if (frontierLine < total - 1) {
        var veilY = Math.round((Math.max(frontierLine, 0) / total) * devH);
        ctx.fillStyle = c.pending;
        ctx.globalAlpha = ordinaryAlpha;
        ctx.fillRect(laneRx, veilY, laneRw, devH - veilY);
        ctx.globalAlpha = 1;
      }
    }

    // Search matches on top.
    var matchRows = bucketRows(this._matchLines);
    ctx.fillStyle = c.match;
    matchRows.forEach(function (count, row) {
      ctx.globalAlpha = Math.min(1, 0.55 + count * 0.15) * searchAlpha;
      ctx.fillRect(laneRx, row, laneRw, tickH);
    });
    ctx.globalAlpha = 1;

    if (this._activeLine >= 0) {
      var ay = markerRow(this._activeLine);
      var activeExpand = Math.round(2 * dpr);
      ctx.fillStyle = c.activeMatch;
      ctx.globalAlpha = importantAlpha;
      ctx.fillRect(laneRx - activeExpand, ay, laneRw + activeExpand, tickH);
      ctx.globalAlpha = 1;
    }

    // Left lane: command marks (exit code colors them; discovered marks stay
    // neutral), with bookmarks painted over — explicit beats inferred. Failed
    // commands paint in a second pass so an overlapping success can never bury
    // a red tick: the lane answers "where did it break".
    var laneLx = visualLeft + Math.round(1 * dpr);
    var laneLw = Math.round(4 * dpr);
    for (var pass = 0; pass < 2; pass++) {
      for (var m = 0; m < this._cmdMarks.length; m++) {
        var cm = this._cmdMarks[m];
        var failed = cm.exit !== null && cm.exit !== 0;
        if ((pass === 1) !== failed) continue;
        var cy = markerRow(cm.marker.line);
        // In Calm mode, routine success is structure, not an alert. Keep only failures red.
        ctx.fillStyle = failed ? c.cmdFail : (calm ? c.cmdUnknown : (cm.exit === 0 ? c.cmdOk : c.cmdUnknown));
        ctx.globalAlpha = failed ? importantAlpha : ordinaryAlpha;
        ctx.fillRect(laneLx, cy, laneLw, tickH);
      }
    }
    ctx.globalAlpha = importantAlpha;
    ctx.fillStyle = c.bookmark;
    for (var b = 0; b < this._bookmarks.length; b++) {
      var by = markerRow(this._bookmarks[b].marker.line);
      ctx.fillRect(laneLx, by, laneLw, tickH);
    }
    ctx.globalAlpha = 1;

    // Viewport window / thumb. It is separate from the canvas so its opacity can
    // animate without hiding or continuously repainting the annotated marks below it.
    var thumbTop = (buf.viewportY / total) * cssH;
    var thumbH = Math.max((term.rows / total) * cssH, 20);
    this._thumb.style.top = Math.round(Math.min(thumbTop, cssH - thumbH)) + "px";
    this._thumb.style.height = Math.round(thumbH) + "px";
  };

  window.RulerAddon = { RulerAddon: RulerAddon };
})();
