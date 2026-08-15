/**
 * Keyword-highlight addon for xterm.js (Sessions' own, not vendored).
 *
 * Scans only the rows currently in the viewport — never the raw output stream —
 * and paints regex matches via the decorations API. Rows are cached by absolute
 * buffer line and rescanned only when their text changes, so the recurring
 * onRender storm (each decoration paint triggers a render) settles immediately.
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
    this._rows = new Map();      // absolute buffer line -> { text, decos: [] }
    this._disposables = [];
    this._scanQueued = false;
  }

  HighlightAddon.prototype.activate = function (term) {
    var self = this;
    this._term = term;
    this._disposables.push(term.onRender(function () { self._queueScan(); }));
    this._disposables.push(term.onResize(function () { self._clear(); self._queueScan(); }));
  };

  HighlightAddon.prototype.dispose = function () {
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
    });
    this._rows.clear();
  };

  // Coalesce the scans triggered by render events (including renders our own
  // decorations cause) into one pass per animation frame.
  HighlightAddon.prototype._queueScan = function () {
    var self = this;
    if (this._scanQueued) return;
    this._scanQueued = true;
    requestAnimationFrame(function () {
      self._scanQueued = false;
      try {
        self._scan();
      } catch (err) {
        if (window.__pageTrace) window.__pageTrace("highlight scan: " + (err && err.message));
      }
    });
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

    // Drop cache entries that scrolled out of the viewport; their decorations
    // wouldn't render anyway, and unbounded growth is a leak.
    var self = this;
    var stale = [];
    this._rows.forEach(function (entry, line) {
      if (line < top || line >= bottom) stale.push(line);
    });
    for (var i = 0; i < stale.length; i++) {
      var gone = this._rows.get(stale[i]);
      for (var d = 0; d < gone.decos.length; d++) gone.decos[d].dispose();
      this._rows.delete(stale[i]);
    }

    for (var line = top; line < bottom; line++) {
      var bufLine = buf.getLine(line);
      if (!bufLine) continue;

      var row = rowText(bufLine);
      var cached = this._rows.get(line);
      if (cached && cached.text === row.text) continue;

      if (cached) {
        for (var j = 0; j < cached.decos.length; j++) cached.decos[j].dispose();
      }
      var decos = row.text.length > 0 ? this._decorateRow(line, row) : [];
      this._rows.set(line, { text: row.text, decos: decos });
    }
  };

  /** Line text plus per-code-unit start/end column maps (wide chars occupy 2 columns,
   * multi-code-unit chars occupy 1). Regex indexes map through these to cells. */
  function rowText(bufLine) {
    var text = "";
    var starts = [];
    var ends = [];
    for (var x = 0; x < bufLine.length; x++) {
      var cell = bufLine.getCell(x);
      if (!cell) break;
      var width = cell.getWidth();
      if (width === 0) continue; // trailing half of a wide char
      var chars = cell.getChars() || " ";
      for (var k = 0; k < chars.length; k++) {
        text += chars[k];
        starts.push(x);
        ends.push(x + width);
      }
    }
    return { text: text.replace(/\s+$/, ""), starts: starts, ends: ends };
  }

  HighlightAddon.prototype._decorateRow = function (line, row) {
    var term = this._term;
    var buf = term.buffer.active;
    var decos = [];
    var budget = MAX_MATCHES_PER_ROW;

    for (var r = 0; r < this._rules.length && budget > 0; r++) {
      var rule = this._rules[r];
      rule.re.lastIndex = 0;
      var m;
      while (budget > 0 && (m = rule.re.exec(row.text)) !== null) {
        if (m[0].length === 0) { // zero-length match: step forward, don't spin
          rule.re.lastIndex++;
          continue;
        }
        var startCol = row.starts[m.index];
        var endCol = row.ends[m.index + m[0].length - 1];
        var deco = this._decorate(line, buf, startCol, endCol - startCol, rule);
        if (deco) {
          decos.push(deco);
          budget--;
        }
      }
    }
    return decos;
  };

  HighlightAddon.prototype._decorate = function (line, buf, x, width, rule) {
    // registerMarker offsets are relative to the cursor's absolute line.
    var marker = this._term.registerMarker(line - (buf.baseY + buf.cursorY));
    if (!marker) return null;
    var deco = this._term.registerDecoration({
      marker: marker,
      x: x,
      width: width,
      layer: "bottom",
      foregroundColor: rule.color
    });
    if (!deco) {
      marker.dispose();
      return null;
    }
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
    var ruleDeco = {
      dispose: function () {
        deco.dispose();
        marker.dispose();
      }
    };
    return ruleDeco;
  };

  window.HighlightAddon = { HighlightAddon: HighlightAddon };
})();
