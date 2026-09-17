/**
 * Keyword-highlight addon for xterm.js (resesh's own, not vendored).
 *
 * Scans only the rows currently in the viewport — never the raw output stream —
 * and paints regex matches via the decorations API. Marker-backed cache entries
 * follow lines through scrollback trimming. Parse events update decorations before
 * the next paint; decoration-only renders never schedule another scan.
 *
 * Rendering notes:
 *  - color   -> decoration foregroundColor (cell text recolored by the renderer)
 *  - bold    -> translucent background tint of the rule color (the decorations API
 *               cannot re-weight glyphs; tint gives the intended extra emphasis)
 *  - underline -> bottom border on the decoration's overlay element
 *  - The alternate buffer (vim/htop) is never scanned: markers only exist in the
 *    normal buffer, and highlighting full-screen apps would be wrong anyway.
 */
(function () {
  "use strict";

  var MAX_MATCHES_PER_ROW = 40;

  function HighlightAddon() {
    this._term = null;
    this._rules = [];            // { id, re, color, tint, underline }
    this._rows = new Map();      // current buffer line -> { text, marker, decos: [] }
    this._nextRows = new Map();  // reused while reconciling marker positions
    this._disposables = [];
    this._scanFrame = null;
  }

  HighlightAddon.prototype.activate = function (term) {
    var self = this;
    this._term = term;
    this._disposables.push(term.onWriteParsed(function () { self._scanNow(); }));
    this._disposables.push(term.onScroll(function () { self._queueScan(); }));
    this._disposables.push(term.onResize(function () { self._clear(); self._queueScan(); }));
  };

  HighlightAddon.prototype.dispose = function () {
    if (this._scanFrame !== null) cancelAnimationFrame(this._scanFrame);
    this._scanFrame = null;
    this._clear();
    for (var i = 0; i < this._disposables.length; i++) this._disposables[i].dispose();
    this._disposables = [];
    this._term = null;
  };

  /** rules: [{ id, pattern, color, bold, underline, matchCase }] — replaces the active set. */
  HighlightAddon.prototype.setRules = function (rules) {
    var compiled = [];
    for (var i = 0; i < (rules || []).length; i++) {
      var r = rules[i];
      try {
        compiled.push({
          id: r.id,
          re: new RegExp(r.pattern, r.matchCase ? "g" : "gi"),
          color: r.color || "#ffffff",
          tint: r.bold ? toTint(r.color || "#ffffff") : null,
          underline: !!r.underline
        });
      } catch (err) {
        // Invalid in this engine (host-side validation is .NET) — skip just this rule.
      }
    }
    this._rules = compiled;
    this._clear();
    this._queueScan();
  };

  /** #rrggbb -> translucent rgba() for the "bold" emphasis tint. Applied to the
   * decoration overlay element, NOT the decoration backgroundColor option: the
   * renderer strips alpha from decoration cell backgrounds, which would paint an
   * opaque block in the same color as the foreground. The overlay sits above the
   * text plane, so it must stay translucent. */
  function toTint(color) {
    var m = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(color);
    if (!m) return null;
    return "rgba(" + parseInt(m[1], 16) + "," + parseInt(m[2], 16) + "," + parseInt(m[3], 16) + ",0.22)";
  }

  HighlightAddon.prototype._clear = function () {
    this._rows.forEach(function (entry) {
      for (var i = 0; i < entry.decos.length; i++) entry.decos[i].dispose();
      entry.marker.dispose();
    });
    this._rows.clear();
  };

  // Scrolling and option changes may happen outside a write. Parsed output cancels
  // this pending pass and scans immediately, instead of waiting an extra frame.
  HighlightAddon.prototype._queueScan = function () {
    var self = this;
    if (this._scanFrame !== null || !this._term || this._rules.length === 0) return;
    this._scanFrame = requestAnimationFrame(function () { self._scanNow(); });
  };

  HighlightAddon.prototype._scanNow = function () {
    if (this._scanFrame !== null) cancelAnimationFrame(this._scanFrame);
    this._scanFrame = null;
    try {
      this._scan();
    } catch (err) {
      if (window.__pageTrace) window.__pageTrace("highlight scan: " + (err && err.message));
    }
  };

  HighlightAddon.prototype._scan = function () {
    var term = this._term;
    if (!term) return;
    var buf = term.buffer.active;
    if (buf.type === "alternate" || this._rules.length === 0) {
      if (this._rows.size > 0) this._clear();
      return;
    }

    var top = buf.viewportY;
    var bottom = top + term.rows;

    // Absolute indices stop advancing when scrollback fills, but markers continue
    // moving. Re-key by their live positions so unchanged rows keep their decorations.
    var nextRows = this._nextRows;
    this._rows.forEach(function (entry) {
      var line = entry.marker.line;
      if (!entry.marker.isDisposed && line >= top && line < bottom) {
        nextRows.set(line, entry);
      } else {
        for (var d = 0; d < entry.decos.length; d++) entry.decos[d].dispose();
        entry.marker.dispose();
      }
    });
    this._rows.clear();
    this._nextRows = this._rows;
    this._rows = nextRows;

    for (var line = top; line < bottom; line++) {
      var bufLine = buf.getLine(line);
      if (!bufLine) continue;

      // The cheap text comparison avoids per-cell objects and Unicode column maps
      // for unchanged rows. Build those maps only if a changed row actually matches.
      var text = bufLine.translateToString(true).replace(/\s+$/, "");
      var cached = this._rows.get(line);
      if (cached && cached.text === text) continue;

      if (cached) {
        for (var j = 0; j < cached.decos.length; j++) cached.decos[j].dispose();
      } else {
        var marker = term.registerMarker(line - (buf.baseY + buf.cursorY));
        if (!marker) continue;
        cached = { text: "", marker: marker, decos: [] };
        this._rows.set(line, cached);
      }
      cached.text = text;
      cached.decos = text.length > 0 ? this._decorateRow(cached.marker, bufLine, text) : [];
    }
  };

  /** Per-code-unit start/end column maps. Wide and combined characters map regex
   * indices to their full terminal cells rather than JavaScript string positions. */
  function rowColumns(bufLine, cell) {
    var starts = [];
    var ends = [];
    for (var x = 0; x < bufLine.length; x++) {
      cell = bufLine.getCell(x, cell);
      if (!cell) break;
      var width = cell.getWidth();
      if (width === 0) continue; // trailing half of a wide char
      var chars = cell.getChars() || " ";
      for (var k = 0; k < chars.length; k++) {
        starts.push(x);
        ends.push(x + width);
      }
    }
    return { starts: starts, ends: ends };
  }

  HighlightAddon.prototype._decorateRow = function (marker, bufLine, text) {
    var row = null;
    var decos = [];
    var budget = MAX_MATCHES_PER_ROW;

    for (var r = 0; r < this._rules.length && budget > 0; r++) {
      var rule = this._rules[r];
      rule.re.lastIndex = 0;
      var m;
      while (budget > 0 && (m = rule.re.exec(text)) !== null) {
        if (m[0].length === 0) { // zero-length match: step forward, don't spin
          rule.re.lastIndex++;
          continue;
        }
        if (!row) row = rowColumns(bufLine, this._term.buffer.active.getNullCell());
        var startCol = row.starts[m.index];
        var endCol = row.ends[m.index + m[0].length - 1];
        var deco = this._decorate(marker, startCol, endCol - startCol, rule);
        if (deco) {
          decos.push(deco);
          budget--;
        }
      }
    }
    return decos;
  };

  HighlightAddon.prototype._decorate = function (marker, x, width, rule) {
    var deco = this._term.registerDecoration({
      marker: marker,
      x: x,
      width: width,
      layer: "bottom",
      foregroundColor: rule.color
    });
    if (!deco) return null;
    if (rule.underline || rule.tint) {
      var color = rule.color;
      var tint = rule.tint;
      var underline = rule.underline;
      deco.onRender(function (element) {
        if (underline) {
          element.style.boxSizing = "border-box";
          element.style.borderBottom = "1px solid " + color;
        }
        if (tint) element.style.backgroundColor = tint;
      });
    }
    return deco;
  };

  window.HighlightAddon = { HighlightAddon: HighlightAddon };
})();
