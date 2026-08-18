/**
 * Annotated-scrollbar addon for xterm.js (Sessions' own, not vendored) — ROADMAP Phase 9.2.
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
 *   - Command marks (Phase 9.4) have two sources feeding one lane:
 *       exact — OSC 133 (FinalTerm) sequences from an integrated shell. A/B remember
 *       the prompt line, C commits a mark there (a command actually ran), D attaches
 *       the exit code, which colors the tick (ok/fail). Shells that emit A/D but no C
 *       still get marks committed on D.
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
 *     Both sources also hand the command TEXT to the page (onCommand hook): the tab's
 *     subtitle shows what is running between prompt titles, which only refresh after
 *     a command ends. 133;D reports the end; for discovered commands the host treats
 *     the next prompt-shaped title as the end.
 *   - Line timestamps (Phase 9.5) arrive from the native host with each SSH read,
 *     before its 16 ms output batch combines reads. A compact virtual-line map keeps
 *     them aligned through scrollback trimming. The page snapshots logical-line times
 *     around xterm reflow so a font or window-size change does not detach them.
 *   - The alternate buffer (vim/htop) has no scrollback: the ruler hides itself.
 */
(function () {
  "use strict";

  var WIDTH = 14;           // CSS px
  var CALM_WIDTH = 10;      // visible CSS px; the pointer target remains WIDTH
  var SCAN_SLICE = 4096;    // lines per animation-frame slice
  var RESCAN_DEBOUNCE = 250;
  var HOVER_DELAY = 150;
  var SNAP_PX = 8;          // click snaps to a mark within this many CSS px
  var DRAG_THRESHOLD = 3;   // pointer movement below this is a click, not a drag
  var FLASH_MS = 700;

  var HL_MAX_RULES = 32;    // bitmask width; overview rules beyond this are ignored
  var HL_SLICE = 2048;      // indexer lines per pass when no idle deadline is available
  var HL_IDLE_MIN_MS = 3;   // stop an idle pass when less than this remains
  var HL_REANCHOR_GAP = 4096; // re-anchor the sentinel once the cursor is this far past it
  var HL_TICK_ALPHA = 0.8;  // slight dim keeps opaque search ticks dominant on top

  var CMD_ECHO_SETTLE_MS = 300; // Enter -> probe evaluation: wait for the echo round trip
  var CMD_ECHO_RETRY_MS = 900;  // one retry for laggy links before the probe gives up

  var TIME_REANCHOR_GAP = 4096;

  // Prompt shapes for discovered command marks: an optional body — bracketed
  // ("[user@host ~]") or space-free ("user@host:~/dir", "Switch(config-if)") — then a
  // $ # % > terminator, an optional space, and a non-space (the command; an empty
  // prompt never marks). Matches Linux default PS1s, Cisco/Junos-style prompts, root
  // shells, and REPLs like "mysql>"; fancy unicode prompts belong to hosts whose
  // owners can install the OSC 133 snippet instead. The capture is the command text,
  // which also feeds the tab's running-command title.
  var CMD_PROMPT_RE = /^(?:\[[^\]]{1,100}\]|[^\s$#%>]{0,100})[$#%>]\s?(\S.*)$/;

  function RulerAddon() {
    this._term = null;
    this._disposables = [];
    this._strip = null;
    this._canvas = null;
    this._tooltip = null;
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

    this._cmdMarks = [];      // [{ marker, exit, src }] src "osc"|"guess"; exit int or null
    this._cmdOscSeen = false; // a shell spoke OSC 133: discovery defers to it from then on
    this._cmdPromptLine = -1; // absolute line of the last OSC 133;A/B prompt start
    this._cmdPromptCol = -1;  // cursor column at OSC 133;B — where the typed command starts
    this._cmdPending = null;  // mark committed by C, awaiting its D exit code
    this.onCommand = null;    // page hook: (text, epoch?) when a command starts, ("") on 133;D

    this._timeWrites = [];    // serialized xterm writes: { data, unixMs|null }
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
    this._drag = null;        // { startY, moved }
    this._hoverTimer = null;
    this._flash = null;       // { deco, marker, timer }
    this._isSplit = false;
    this._isGroupFocused = true;
    this._isPointerOver = false;
  }

  RulerAddon.prototype.activate = function (term) {
    var self = this;
    this._term = term;

    var strip = document.createElement("div");
    strip.className = "scroll-ruler";
    strip.style.cssText = "position:absolute;top:0;right:0;bottom:0;width:" + WIDTH +
      "px;z-index:30;cursor:default;user-select:none;";
    var canvas = document.createElement("canvas");
    canvas.style.cssText = "position:absolute;top:0;left:0;width:100%;height:100%;";
    strip.appendChild(canvas);
    // xterm's element is only as tall as its whole character rows, which can leave
    // unused pixels below it after fitting. Mount the ruler in the full-height host
    // so the scrollbar reaches the actual bottom edge of the content pane.
    var host = term.element.parentElement || term.element;
    host.appendChild(strip);
    this._strip = strip;
    this._canvas = canvas;

    var tooltip = document.createElement("div");
    tooltip.className = "scroll-ruler-tooltip";
    tooltip.style.cssText = "position:absolute;right:" + (WIDTH + 6) +
      "px;display:none;z-index:31;max-width:420px;padding:5px 10px;border-radius:6px;" +
      "font-family:'Cascadia Mono',Consolas,monospace;font-size:12px;line-height:16px;" +
      "white-space:normal;overflow:hidden;box-shadow:0 6px 18px rgba(0,0,0,0.35);" +
      "pointer-events:none;";
    host.appendChild(tooltip);
    this._tooltip = tooltip;

    this._disposables.push(term.onScroll(function () { self._queuePaint(); }));
    this._disposables.push(term.onResize(function () {
      self._queuePaint();
      if (self._search) self._scheduleRescan();
      self._hlRebuild(); // reflow rewraps rows; absolute line numbers all move
    }));
    this._disposables.push(term.onWriteParsed(function () {
      self._queuePaint();
      if (self._search) self._scheduleRescan();
    }));
    this._disposables.push(term.onLineFeed(function () {
      self._timeStampCurrent();
      self._hlKick();
    }));
    if (term.parser && term.parser.registerOscHandler) {
      this._disposables.push(term.parser.registerOscHandler(133, function (data) {
        try {
          self._onOsc133(data);
        } catch (err) {
          if (window.__pageTrace) window.__pageTrace("ruler osc133: " + (err && err.message));
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
      self._queuePaint();
    });
    strip.addEventListener("pointerdown", function (e) { self._onPointerDown(e); });
    strip.addEventListener("pointermove", function (e) { self._onPointerMove(e); });
    strip.addEventListener("pointerup", function (e) { self._onPointerUp(e); });
    strip.addEventListener("pointerleave", function () {
      self._isPointerOver = false;
      self._hideTooltip();
      self._queuePaint();
    });
    strip.addEventListener("wheel", function (e) {
      e.preventDefault();
      var lines = Math.round(e.deltaY / 40) || (e.deltaY > 0 ? 1 : -1);
      self._term.scrollLines(lines);
    }, { passive: false });

    this._resizeObserver = new ResizeObserver(function () { self._queuePaint(); });
    this._resizeObserver.observe(strip);

    this._queuePaint();
  };

  RulerAddon.prototype.dispose = function () {
    for (var i = 0; i < this._disposables.length; i++) this._disposables[i].dispose();
    this._disposables = [];
    for (var b = 0; b < this._bookmarks.length; b++) this._bookmarks[b].marker.dispose();
    this._bookmarks = [];
    var cmdMarks = this._cmdMarks.slice(); // onDispose handlers splice the live array
    for (var m = 0; m < cmdMarks.length; m++) cmdMarks[m].marker.dispose();
    this._cmdMarks = [];
    this._cmdPending = null;
    this._timeWrites = [];
    this._timeWriteActive = false;
    this._timeActiveUnixMs = null;
    this._timeIndex.clear();
    if (this._timeAnchor) { var timeAnchor = this._timeAnchor; this._timeAnchor = null; timeAnchor.marker.dispose(); }
    this._hlRules = [];
    this._hlIndex.clear();
    if (this._hlAnchor) { var anchor = this._hlAnchor; this._hlAnchor = null; anchor.marker.dispose(); }
    this._clearFlash();
    if (this._resizeObserver) this._resizeObserver.disconnect();
    if (this._strip && this._strip.parentElement) this._strip.parentElement.removeChild(this._strip);
    if (this._tooltip && this._tooltip.parentElement) this._tooltip.parentElement.removeChild(this._tooltip);
    this._term = null;
  };

  /** Partial override of the color set; keys as in the constructor default. */
  RulerAddon.prototype.setTheme = function (colors) {
    for (var k in colors) this._colors[k] = colors[k];
    this._queuePaint();
  };

  /** Full presentation in one group; a quieter, narrower rail in split mode.
   * Hover always restores full detail so marks remain easy to inspect. */
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
    if (this._timeWriteActive || !this._term || this._timeWrites.length === 0) return;
    var self = this;
    var next = this._timeWrites.shift();
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
        this._cmdPending = this._cmdCommit(this._cmdPromptLine, null, "osc");
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
      } else if (this._cmdPromptLine >= 0) {
        // Shell emits A/D but never C: this D still belongs to whatever was typed
        // at the last prompt (empty Enters get a mark too — indistinguishable).
        this._cmdCommit(this._cmdPromptLine, exit, "osc");
        this._cmdPromptLine = -1;
      }
      this._fireCommand("", undefined); // the command is over, whatever it was
    }
  };

  /** First logical line of the buffer starting at (row, col), following soft wraps —
   * the command text for the running-command title. col -1 means "unknown" (no 133;B):
   * fall back to the prompt regex. Capped: a title needs a name, not the whole paste. */
  RulerAddon.prototype._cmdText = function (buf, row, col) {
    var line = buf.getLine(row);
    if (!line) return "";
    var full = line.translateToString(true);
    if (col < 0) {
      var m = CMD_PROMPT_RE.exec(full);
      if (!m) return "";
      col = full.length - m[1].length;
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
    if (!this.onCommand) return;
    try {
      this.onCommand(text, epoch);
    } catch (err) {
      if (window.__pageTrace) window.__pageTrace("ruler onCommand: " + (err && err.message));
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
    var marker = this._term.registerMarker(0);
    if (!marker) return;
    var self = this;
    var attempts = 0;
    var reported = false;
    function evaluate() {
      if (marker.isDisposed) return; // trimmed away while waiting
      if (self._cmdOscSeen || !self._term) { marker.dispose(); return; }
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
      var m = CMD_PROMPT_RE.exec(lineText);
      if (m) {
        if (!reported) {
          reported = true;
          self._fireCommand(self._cmdText(norm, row, lineText.length - m[1].length), epoch);
        }
        if (self._term.buffer.active.type !== "alternate") {
          self._cmdCommit(row, null, "guess");
          marker.dispose();
          return;
        }
      }
      if (attempts < 2) setTimeout(evaluate, CMD_ECHO_RETRY_MS);
      else marker.dispose();
    }
    setTimeout(evaluate, CMD_ECHO_SETTLE_MS);
  };

  /** Adds a command mark at an absolute line (idempotent per line; an exit code
   * updates an existing mark in place). Markers keep marks trim-safe for free. */
  RulerAddon.prototype._cmdCommit = function (line, exit, src) {
    for (var i = 0; i < this._cmdMarks.length; i++) {
      if (this._cmdMarks[i].marker.line === line) {
        if (exit !== null) {
          this._cmdMarks[i].exit = exit;
          this._queuePaint();
        }
        return this._cmdMarks[i];
      }
    }
    var buf = this._term.buffer.active;
    var marker = this._term.registerMarker(line - (buf.baseY + buf.cursorY));
    if (!marker) return null;
    var self = this;
    var entry = { marker: marker, exit: exit, src: src };
    marker.onDispose(function () {
      var idx = self._cmdMarks.indexOf(entry);
      if (idx >= 0) self._cmdMarks.splice(idx, 1);
      if (self._cmdPending === entry) self._cmdPending = null;
      self._queuePaint();
    });
    this._cmdMarks.push(entry);
    this._queuePaint();
    return entry;
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
    this._drag = { startY: e.offsetY, moved: false };
    try { this._strip.setPointerCapture(e.pointerId); } catch (err) {}
  };

  RulerAddon.prototype._onPointerMove = function (e) {
    if (this._drag) {
      if (!this._drag.moved && Math.abs(e.offsetY - this._drag.startY) < DRAG_THRESHOLD) return;
      this._drag.moved = true;
      this._scrollLineToCenter(this._lineAtY(e.offsetY));
      return;
    }
    this._scheduleTooltip(e.offsetY);
  };

  RulerAddon.prototype._onPointerUp = function (e) {
    if (!this._drag) return;
    var wasClick = !this._drag.moved;
    this._drag = null;
    try { this._strip.releasePointerCapture(e.pointerId); } catch (err) {}
    if (!wasClick) return;

    var line = this._lineAtY(e.offsetY);
    var snapped = this._snapToMark(e.offsetY);
    if (snapped >= 0) line = snapped;
    this._scrollLineToCenter(line);
    this._flashLine(line);
    this._term.focus();
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

    var commands = 0, soleCommand = null;
    for (var cm = 0; cm < this._cmdMarks.length; cm++) {
      var cl = this._cmdMarks[cm].marker.line;
      if (cl >= lo && cl <= hi) {
        commands++;
        soleCommand = this._cmdMarks[cm];
      }
    }

    var sampleLine = firstMatch >= 0 ? firstMatch
      : firstHl >= 0 ? firstHl
      : soleCommand ? soleCommand.marker.line
      : line;

    var metadata = [];
    function addMetadata(text, color) { metadata.push({ text: text, color: color || null }); }
    if (commands === 1) {
      addMetadata(soleCommand.exit === null ? "command" : "exit " + soleCommand.exit);
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

    var c = this._colors;
    var tip = this._tooltip;
    this._renderTooltip(tip, sample, metadata);
    tip.style.background = c.tooltipBg;
    tip.style.color = c.tooltipFg;
    tip.style.border = "1px solid " + c.tooltipBorder;
    tip.style.display = "block";
    var tipH = tip.offsetHeight;
    tip.style.top = Math.max(0, Math.min(y - tipH / 2, h - tipH)) + "px";
  };

  /** Render the tooltip as a compact command card: command/sample first, metadata below.
   * Build DOM with textContent only; terminal output is untrusted. Plain-object test doubles
   * use the newline fallback. */
  RulerAddon.prototype._renderTooltip = function (tip, sample, metadata) {
    var prompt = "", command = sample;
    var match = sample.match(/^((?:\[[^\]]{1,100}\]|[^\s$#%>]{0,100})[$#%>])\s?(.*)$/);
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
  };

  RulerAddon.prototype._hideTooltip = function () {
    if (this._hoverTimer) { clearTimeout(this._hoverTimer); this._hoverTimer = null; }
    if (this._tooltip) this._tooltip.style.display = "none";
  };

  // ---- painting ----

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
      return;
    }
    this._strip.style.display = "block";

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
    var visualWidth = Math.round((calm ? CALM_WIDTH : WIDTH) * dpr);
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

    var laneRx = visualLeft + Math.round((calm ? 5 : 6) * dpr);
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
      var activeExpand = Math.round((calm ? 1 : 2) * dpr);
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
    var laneLw = Math.round((calm ? 3 : 4) * dpr);
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

    // Viewport window / thumb.
    var thumbTop = (buf.viewportY / total) * devH;
    var thumbH = Math.max((term.rows / total) * devH, Math.round(20 * dpr));
    ctx.fillStyle = c.thumb;
    ctx.fillRect(visualLeft, Math.round(Math.min(thumbTop, devH - thumbH)), visualWidth, Math.round(thumbH));
  };

  window.RulerAddon = { RulerAddon: RulerAddon };
})();
