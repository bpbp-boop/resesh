// reseshvt: the C side of resesh's libghostty-vt terminal surface.
//
// Owns one GhosttyTerminal per tab behind an SRW lock so the backend reader thread can parse
// while the UI thread reads frames and encodes input. Everything whose layout the library
// marks unstable (sized structs, implicit enum values, callback payloads) stays in this file;
// C# sees flat structs and one event callback.
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ghostty/vt.h>

#define RVT_API __declspec(dllexport)

typedef struct { void* ptr; } RvtSrwLock;
__declspec(dllimport) void __stdcall InitializeSRWLock(RvtSrwLock* lock);
__declspec(dllimport) void __stdcall AcquireSRWLockExclusive(RvtSrwLock* lock);
__declspec(dllimport) void __stdcall ReleaseSRWLockExclusive(RvtSrwLock* lock);

#define RVT_ABI_VERSION 3

// ---- events -------------------------------------------------------------------------------

enum {
  RVT_EVENT_PTY = 1,        // bytes the terminal answers with (DA, DSR, ...): send to the host
  RVT_EVENT_BELL = 2,
  RVT_EVENT_TITLE = 3,      // UTF-8 title
  RVT_EVENT_PWD = 4,        // raw OSC 7 URI or OSC 9;9 / 1337 path
  RVT_EVENT_CLIPBOARD = 5,  // UTF-8 text a program copied (OSC 52)
  RVT_EVENT_OSC = 6,        // unknown OSC content, "number;payload"
  RVT_EVENT_SEMANTIC = 7,   // RvtSemanticEvent followed by the UTF-8 command line (OSC 133)
};

// Shell integration step, captured while the output is parsed (the C# side handles it
// later, after more output may have arrived, so positions are taken here).
typedef struct {
  int32_t kind;          // 1 prompt start, 2 input start, 3 output start, 4 command end
  int32_t prompt_kind;   // 0 primary, 1 right, 2 continuation, 3 secondary
  int32_t has_exit;
  int32_t exit_code;
  int32_t cursor_line;   // absolute line (scrollback + active row)
  int32_t cursor_x;
  uint32_t marker;       // marker at the cursor line, owned by the receiver (0 if none)
  uint32_t command_len;  // bytes of command text that follow this struct
} RvtSemanticEvent;

#define RVT_MAX_MARKERS 4096

// Called synchronously on the thread that wrote output, with the terminal lock held. The
// handler must copy what it needs and must not call back into this library.
typedef void (*RvtEventFn)(void* user, int kind, const uint8_t* data, size_t len);

// ---- cells --------------------------------------------------------------------------------

#define RVT_BOLD 1u
#define RVT_ITALIC 2u
#define RVT_UNDERLINE 4u
#define RVT_STRIKE 8u
#define RVT_DEFAULT_BG 16u
#define RVT_FAINT 32u
#define RVT_INVISIBLE 64u
#define RVT_SELECTED 128u
#define RVT_MATCH 256u
#define RVT_MATCH_CURRENT 512u
#define RVT_MAX_SPANS 512

typedef struct { uint16_t y, x0, x1, current; } RvtSpan;

typedef struct {
  uint32_t cp;      // base codepoint, 0 when empty
  uint32_t fg;      // 0x00RRGGBB, inverse applied
  uint32_t bg;      // 0x00RRGGBB, inverse applied
  uint16_t flags;   // RVT_*
  uint8_t wide;     // GhosttyCellWide
  uint8_t glen;     // grapheme codepoint count
} RvtCell;

typedef struct {
  uint32_t default_fg;
  uint32_t default_bg;
  uint32_t cursor_color;
  uint16_t cursor_x;
  uint16_t cursor_y;
  uint8_t cursor_visible;
  uint8_t cursor_style;   // 0 block, 1 bar, 2 underline, 3 hollow block
  uint8_t cursor_blinking;
  uint8_t dirty;          // GhosttyRenderStateDirty before cleaning
  uint16_t dirty_rows;
  uint16_t alternate;     // the alternate screen is active
  uint64_t scroll_total;  // rows in the scrollable area
  uint64_t scroll_offset; // first viewport row within it
  uint64_t scroll_len;    // viewport rows
} RvtFrameInfo;

typedef struct {
  GhosttyTerminal term;
  GhosttyRenderState rs;
  GhosttyRenderStateRowIterator it;
  GhosttyRenderStateRowCells cells;
  GhosttyKeyEncoder keys;
  GhosttyKeyEvent key;
  GhosttyMouseEncoder mouse;
  GhosttyMouseEvent mouse_event;
  GhosttyTrackedGridRef markers[RVT_MAX_MARKERS]; // index = marker id - 1
  uint32_t marker_hint;
  GhosttySearch search;
  GhosttySelectionGesture gesture;
  GhosttySelectionGestureEvent gesture_events[4]; // press, release, drag, autoscroll tick
  bool searching;
  RvtSpan spans[RVT_MAX_SPANS];
  int span_count;
  uint32_t span_hash;
  GhosttySelection match_buf[RVT_MAX_SPANS];
  RvtSrwLock lock;
  RvtEventFn on_event;
  void* user;
  uint32_t cell_w, cell_h, screen_w, screen_h;
} RvtTerm;

static void lock(RvtTerm* t) { AcquireSRWLockExclusive(&t->lock); }
static void unlock(RvtTerm* t) { ReleaseSRWLockExclusive(&t->lock); }
static uint32_t pack(GhosttyColorRgb c) { return ((uint32_t)c.r << 16) | ((uint32_t)c.g << 8) | c.b; }
static GhosttyColorRgb unpack(uint32_t v) {
  GhosttyColorRgb c = { (uint8_t)(v >> 16), (uint8_t)(v >> 8), (uint8_t)v };
  return c;
}

static void emit(RvtTerm* t, int kind, const uint8_t* data, size_t len) {
  if (t->on_event) t->on_event(t->user, kind, data, len);
}

// ---- markers and lines (caller holds the lock) ---------------------------------------------

static bool alternate_active(RvtTerm* t) {
  GhosttyTerminalScreen screen = GHOSTTY_TERMINAL_SCREEN_PRIMARY;
  ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_ACTIVE_SCREEN, &screen);
  return screen != GHOSTTY_TERMINAL_SCREEN_PRIMARY;
}

static int32_t cursor_line(RvtTerm* t, uint16_t* x) {
  uint16_t cx = 0, cy = 0;
  size_t scrollback = 0;
  ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_CURSOR_X, &cx);
  ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_CURSOR_Y, &cy);
  ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_SCROLLBACK_ROWS, &scrollback);
  if (x) *x = cx;
  return (int32_t)(scrollback + cy);
}

static uint32_t marker_new_locked(RvtTerm* t, int32_t line) {
  if (line < 0) return 0;
  for (uint32_t n = 0; n < RVT_MAX_MARKERS; n++) {
    uint32_t i = (t->marker_hint + n) % RVT_MAX_MARKERS;
    if (t->markers[i]) continue;
    GhosttyPoint pt = { .tag = GHOSTTY_POINT_TAG_SCREEN, .value = { .coordinate = { .x = 0, .y = (uint32_t)line } } };
    if (ghostty_terminal_grid_ref_track(t->term, pt, &t->markers[i]) != GHOSTTY_SUCCESS) {
      t->markers[i] = NULL;
      return 0;
    }
    t->marker_hint = i + 1;
    return i + 1;
  }
  return 0;
}

static void on_write_pty(GhosttyTerminal term, void* user, const uint8_t* data, size_t len) {
  (void)term;
  emit((RvtTerm*)user, RVT_EVENT_PTY, data, len);
}

static void on_bell(GhosttyTerminal term, void* user) {
  (void)term;
  emit((RvtTerm*)user, RVT_EVENT_BELL, NULL, 0);
}

static void on_title(GhosttyTerminal term, void* user) {
  GhosttyString s = { 0 };
  if (ghostty_terminal_get(term, GHOSTTY_TERMINAL_DATA_TITLE, &s) == GHOSTTY_SUCCESS)
    emit((RvtTerm*)user, RVT_EVENT_TITLE, s.ptr, s.len);
}

static void on_pwd(GhosttyTerminal term, void* user) {
  GhosttyString s = { 0 };
  if (ghostty_terminal_get(term, GHOSTTY_TERMINAL_DATA_PWD, &s) == GHOSTTY_SUCCESS)
    emit((RvtTerm*)user, RVT_EVENT_PWD, s.ptr, s.len);
}

static void on_clipboard_write(GhosttyTerminal term, void* user, const GhosttyClipboardWrite* write) {
  (void)term;
  GhosttyClipboardWriteResult result = GHOSTTY_CLIPBOARD_WRITE_RESULT_UNSUPPORTED;
  for (size_t i = 0; i < write->contents_len; i++) {
    const GhosttyClipboardContent* c = &write->contents[i];
    if (c->mime.len >= 5 && memcmp(c->mime.ptr, "text/", 5) == 0) {
      emit((RvtTerm*)user, RVT_EVENT_CLIPBOARD, c->data.ptr, c->data.len);
      result = GHOSTTY_CLIPBOARD_WRITE_RESULT_SUCCESS;
      break;
    }
  }
  if (write->reply) {
    GhosttyClipboardWriteReply reply = GHOSTTY_INIT_SIZED(GhosttyClipboardWriteReply);
    reply.result = result;
    reply.remember = false;
    write->reply(write, &reply);
  }
}

static void on_semantic(GhosttyTerminal term, void* user, const GhosttyTerminalSemanticPrompt* ev) {
  (void)term;
  RvtTerm* t = (RvtTerm*)user;
  if (alternate_active(t)) return; // marks belong to the normal screen, as in terminal.html
  uint8_t buf[sizeof(RvtSemanticEvent) + 4096];
  RvtSemanticEvent* e = (RvtSemanticEvent*)buf;
  memset(e, 0, sizeof *e);
  e->kind = (int32_t)ev->kind;
  e->prompt_kind = (int32_t)ev->prompt_kind;
  e->has_exit = ev->has_exit_code ? 1 : 0;
  e->exit_code = ev->exit_code;
  uint16_t x = 0;
  e->cursor_line = cursor_line(t, &x);
  e->cursor_x = x;
  e->marker = marker_new_locked(t, e->cursor_line);
  size_t n = ev->command.len < 4096 ? ev->command.len : 4096;
  if (n) memcpy(buf + sizeof *e, ev->command.ptr, n);
  e->command_len = (uint32_t)n;
  emit(t, RVT_EVENT_SEMANTIC, buf, sizeof *e + n);
}

static void on_unknown(GhosttyTerminal term, void* user, const GhosttyTerminalUnknownSequence* seq) {
  (void)term;
  if (seq->tag == GHOSTTY_TERMINAL_UNKNOWN_SEQUENCE_OSC && !seq->value.osc.truncated)
    emit((RvtTerm*)user, RVT_EVENT_OSC, seq->value.osc.content.ptr, seq->value.osc.content.len);
}

// OSC 9 / OSC 777 notifications and OSC 9;4 progress are parsed by libghostty-vt; they are
// handed on rebuilt as the raw OSC content terminal.html forwarded ("9;body",
// "777;notify;title;body", "9;4;state;progress"), so the host parses one format.
static void emit_osc_text(RvtTerm* t, const char* head, const GhosttyString* a, const GhosttyString* b) {
  uint8_t buf[4096];
  size_t n = 0;
  size_t head_len = strlen(head);
  memcpy(buf, head, head_len);
  n = head_len;
  const GhosttyString* parts[2] = { a, b };
  for (int i = 0; i < 2; i++) {
    if (!parts[i]) continue;
    if (i == 1) buf[n++] = ';';
    size_t len = parts[i]->len < sizeof buf - n - 1 ? parts[i]->len : sizeof buf - n - 1;
    memcpy(buf + n, parts[i]->ptr, len);
    n += len;
  }
  emit(t, RVT_EVENT_OSC, buf, n);
}

static void on_notification(GhosttyTerminal term, void* user, const GhosttyTerminalDesktopNotification* note) {
  (void)term;
  if (note->title.len > 0)
    emit_osc_text((RvtTerm*)user, "777;notify;", &note->title, &note->body);
  else
    emit_osc_text((RvtTerm*)user, "9;", &note->body, NULL);
}

static void on_progress(GhosttyTerminal term, void* user, const GhosttyTerminalProgressReport* report) {
  (void)term;
  char buf[32];
  int n = report->progress >= 0
    ? snprintf(buf, sizeof buf, "9;4;%d;%d", (int)report->state, (int)report->progress)
    : snprintf(buf, sizeof buf, "9;4;%d", (int)report->state);
  if (n > 0) emit((RvtTerm*)user, RVT_EVENT_OSC, (const uint8_t*)buf, (size_t)n);
}

// CSI > q: Claude Code enables synchronized output only for terminals that answer
// (DECISIONS.md, "XTVERSION reply"); the page answered "Resesh".
static GhosttyString on_xtversion(GhosttyTerminal term, void* user) {
  (void)term; (void)user;
  GhosttyString s = { (const uint8_t*)"Resesh", 6 };
  return s;
}

RVT_API int rvt_abi_version(void) { return RVT_ABI_VERSION; }

RVT_API RvtTerm* rvt_new(uint16_t cols, uint16_t rows, size_t scrollback_lines, RvtEventFn on_event, void* user) {
  RvtTerm* t = (RvtTerm*)calloc(1, sizeof *t);
  if (!t) return NULL;
  InitializeSRWLock(&t->lock);
  t->on_event = on_event;
  t->user = user;
  if (ghostty_terminal_new(NULL, &t->term, cols, rows) != GHOSTTY_SUCCESS ||
      ghostty_render_state_new(NULL, &t->rs) != GHOSTTY_SUCCESS ||
      ghostty_render_state_row_iterator_new(NULL, &t->it) != GHOSTTY_SUCCESS ||
      ghostty_render_state_row_cells_new(NULL, &t->cells) != GHOSTTY_SUCCESS ||
      ghostty_key_encoder_new(NULL, &t->keys) != GHOSTTY_SUCCESS ||
      ghostty_key_event_new(NULL, &t->key) != GHOSTTY_SUCCESS ||
      ghostty_mouse_encoder_new(NULL, &t->mouse) != GHOSTTY_SUCCESS ||
      ghostty_mouse_event_new(NULL, &t->mouse_event) != GHOSTTY_SUCCESS) {
    // Partial construction: free what exists.
    if (t->mouse_event) ghostty_mouse_event_free(t->mouse_event);
    if (t->mouse) ghostty_mouse_encoder_free(t->mouse);
    if (t->key) ghostty_key_event_free(t->key);
    if (t->keys) ghostty_key_encoder_free(t->keys);
    if (t->cells) ghostty_render_state_row_cells_free(t->cells);
    if (t->it) ghostty_render_state_row_iterator_free(t->it);
    if (t->rs) ghostty_render_state_free(t->rs);
    if (t->term) ghostty_terminal_free(t->term);
    free(t);
    return NULL;
  }
  // The default byte cap keeps only a few hundred rows of a wide terminal; the line limit
  // is what the user configures.
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_SCROLLBACK_MAX_BYTES, NULL);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_SCROLLBACK_MAX_LINES, &scrollback_lines);
  // Cluster graphemes (emoji ZWJ sequences, flags, combining marks) into one cell, like
  // Ghostty itself; programs can still turn mode 2027 off.
  GhosttyTerminalModeConfig grapheme = { GHOSTTY_MODE_GRAPHEME_CLUSTER, true };
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_MODE_DEFAULT, &grapheme);
  size_t unknown_max = 4096;
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_UNKNOWN_MAX_BYTES, &unknown_max);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_USERDATA, t);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_WRITE_PTY, (const void*)on_write_pty);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_BELL, (const void*)on_bell);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_TITLE_CHANGED, (const void*)on_title);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_PWD_CHANGED, (const void*)on_pwd);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_CLIPBOARD_WRITE, (const void*)on_clipboard_write);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_UNKNOWN_SEQUENCE, (const void*)on_unknown);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_SEMANTIC_PROMPT, (const void*)on_semantic);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_DESKTOP_NOTIFICATION, (const void*)on_notification);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_PROGRESS_REPORT, (const void*)on_progress);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_XTVERSION, (const void*)on_xtversion);
  return t;
}

RVT_API void rvt_free(RvtTerm* t) {
  if (!t) return;
  lock(t);
  for (int i = 0; i < RVT_MAX_MARKERS; i++)
    if (t->markers[i]) ghostty_tracked_grid_ref_free(t->markers[i]);
  if (t->search) ghostty_search_free(t->search);
  if (t->gesture) ghostty_selection_gesture_free(t->gesture, t->term);
  for (int i = 0; i < 4; i++)
    if (t->gesture_events[i]) ghostty_selection_gesture_event_free(t->gesture_events[i]);
  ghostty_mouse_event_free(t->mouse_event);
  ghostty_mouse_encoder_free(t->mouse);
  ghostty_key_event_free(t->key);
  ghostty_key_encoder_free(t->keys);
  ghostty_render_state_row_cells_free(t->cells);
  ghostty_render_state_row_iterator_free(t->it);
  ghostty_render_state_free(t->rs);
  ghostty_terminal_free(t->term);
  unlock(t);
  free(t);
}

RVT_API void rvt_write(RvtTerm* t, const uint8_t* data, size_t len) {
  lock(t);
  ghostty_terminal_vt_write(t->term, data, len);
  unlock(t);
}

RVT_API void rvt_resize(RvtTerm* t, uint16_t cols, uint16_t rows, uint32_t cell_w, uint32_t cell_h) {
  lock(t);
  t->cell_w = cell_w; t->cell_h = cell_h;
  t->screen_w = cols * cell_w; t->screen_h = rows * cell_h;
  ghostty_terminal_resize(t->term, cols, rows, cell_w, cell_h);
  unlock(t);
}

RVT_API void rvt_set_scrollback(RvtTerm* t, size_t lines) {
  lock(t);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_SCROLLBACK_MAX_LINES, &lines);
  unlock(t);
}

// palette16: the theme's 16 ANSI colors; the 6x6x6 cube and gray ramp stay xterm-standard.
RVT_API void rvt_set_colors(RvtTerm* t, uint32_t fg, uint32_t bg, uint32_t cursor, const uint32_t* palette16) {
  GhosttyColorRgb palette[256];
  ghostty_color_palette_default(palette);
  for (int i = 0; i < 16; i++) palette[i] = unpack(palette16[i]);
  GhosttyColorRgb f = unpack(fg), b = unpack(bg), c = unpack(cursor);
  lock(t);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_COLOR_FOREGROUND, &f);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_COLOR_BACKGROUND, &b);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_COLOR_CURSOR, &c);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_COLOR_PALETTE, palette);
  unlock(t);
}

// ---- frames -------------------------------------------------------------------------------

static uint32_t resolve(GhosttyStyleColor c, const GhosttyRenderStateColors* colors, uint32_t fallback) {
  switch (c.tag) {
    case GHOSTTY_STYLE_COLOR_RGB: return pack(c.value.rgb);
    case GHOSTTY_STYLE_COLOR_PALETTE: return pack(colors->palette[c.value.palette]);
    default: return fallback;
  }
}

static bool same_selection(const GhosttySelection* a, const GhosttySelection* b) {
  return memcmp(&a->start, &b->start, sizeof a->start) == 0 && memcmp(&a->end, &b->end, sizeof a->end) == 0;
}

// Under the lock: refresh the search and turn on-screen matches into per-row spans.
static void collect_spans(RvtTerm* t, uint16_t rows) {
  t->span_count = 0;
  uint32_t hash = 2166136261u;
  if (t->searching && t->search) {
    GhosttySearchStatus status = GHOSTTY_SEARCH_STATUS_FEED_REQUIRED;
    ghostty_search_feed(t->search);
    for (int i = 0; i < 64; i++) {
      if (ghostty_search_tick(t->search, &status) != GHOSTTY_SUCCESS || status != GHOSTTY_SEARCH_STATUS_RUNNING) break;
    }
    GhosttySelectionBuffer buf = { t->match_buf, RVT_MAX_SPANS, 0 };
    GhosttySelection current = GHOSTTY_INIT_SIZED(GhosttySelection);
    bool has_current = ghostty_search_get(t->search, GHOSTTY_SEARCH_DATA_SELECTED_MATCH, &current) == GHOSTTY_SUCCESS;
    // More on-screen matches than the buffer holds: highlight none rather than read garbage.
    if (ghostty_search_get(t->search, GHOSTTY_SEARCH_DATA_VIEWPORT_MATCHES, &buf) == GHOSTTY_SUCCESS) {
      size_t n = buf.len < RVT_MAX_SPANS ? buf.len : RVT_MAX_SPANS;
      for (size_t i = 0; i < n && t->span_count < RVT_MAX_SPANS; i++) {
        GhosttyPointCoordinate a, b;
        if (ghostty_terminal_point_from_grid_ref(t->term, &t->match_buf[i].start, GHOSTTY_POINT_TAG_VIEWPORT, &a) != GHOSTTY_SUCCESS ||
            ghostty_terminal_point_from_grid_ref(t->term, &t->match_buf[i].end, GHOSTTY_POINT_TAG_VIEWPORT, &b) != GHOSTTY_SUCCESS)
          continue;
        uint16_t cur = has_current && same_selection(&t->match_buf[i], &current) ? 1 : 0;
        for (uint32_t y = a.y; y <= b.y && y < rows && t->span_count < RVT_MAX_SPANS; y++) {
          RvtSpan sp = { (uint16_t)y, y == a.y ? a.x : 0, y == b.y ? b.x : 0xFFFF, cur };
          t->spans[t->span_count++] = sp;
          hash = (hash ^ (sp.y | ((uint32_t)sp.x0 << 16))) * 16777619u;
          hash = (hash ^ (sp.x1 | ((uint32_t)sp.current << 16))) * 16777619u;
        }
      }
    }
  }
  t->span_hash = t->span_count ? hash : 0;
}

static void apply_spans(const RvtTerm* t, RvtCell* row, uint16_t y, uint16_t cols) {
  for (int i = 0; i < t->span_count; i++) {
    const RvtSpan* sp = &t->spans[i];
    if (sp->y != y) continue;
    uint16_t end = sp->x1 < cols ? sp->x1 : (uint16_t)(cols - 1);
    for (uint16_t x = sp->x0; x <= end && x < cols; x++) {
      row[x].flags |= sp->current ? RVT_MATCH_CURRENT : RVT_MATCH;
      if (sp->current) row[x].fg = 0x1E1E1E; // dark text on the bright current-match color
    }
  }
}

// Captures the terminal under the lock (cheap), then copies dirty rows into out[y * cols + x]
// outside it, flags them in dirty_rows and cleans the render state. force_all re-reads every
// row (after a resize or a lost frame). Returns the number of rows copied.
RVT_API int rvt_read_frame(RvtTerm* t, RvtCell* out, uint16_t cols, uint16_t rows, uint8_t* dirty_rows,
                           int force_all, RvtFrameInfo* info) {
  GhosttyTerminalScrollbar bar = { 0 };
  lock(t);
  ghostty_render_state_begin_update(t->rs, t->term);
  ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_SCROLLBAR, &bar);
  info->alternate = alternate_active(t) ? 1 : 0;
  uint32_t old_hash = t->span_hash;
  collect_spans(t, rows);
  if (t->span_hash != old_hash) force_all = 1; // highlights moved: repaint every row
  unlock(t);
  ghostty_render_state_end_update(t->rs);
  info->scroll_total = bar.total;
  info->scroll_offset = bar.offset;
  info->scroll_len = bar.len;

  if (force_all) {
    GhosttyRenderStateDirty full = GHOSTTY_RENDER_STATE_DIRTY_FULL;
    ghostty_render_state_set(t->rs, GHOSTTY_RENDER_STATE_OPTION_DIRTY, &full);
  }
  GhosttyRenderStateDirty dirty = GHOSTTY_RENDER_STATE_DIRTY_FALSE;
  ghostty_render_state_get(t->rs, GHOSTTY_RENDER_STATE_DATA_DIRTY, &dirty);
  info->dirty = (uint8_t)dirty;
  info->dirty_rows = 0;

  GhosttyRenderStateCursor cursor = GHOSTTY_INIT_SIZED(GhosttyRenderStateCursor);
  ghostty_render_state_get(t->rs, GHOSTTY_RENDER_STATE_DATA_CURSOR, &cursor);
  info->cursor_visible = cursor.visible && cursor.viewport_has_value;
  info->cursor_x = cursor.viewport_x;
  info->cursor_y = cursor.viewport_y;
  info->cursor_blinking = cursor.blinking;
  switch (cursor.visual_style) {
    case GHOSTTY_RENDER_STATE_CURSOR_VISUAL_STYLE_BAR: info->cursor_style = 1; break;
    case GHOSTTY_RENDER_STATE_CURSOR_VISUAL_STYLE_UNDERLINE: info->cursor_style = 2; break;
    case GHOSTTY_RENDER_STATE_CURSOR_VISUAL_STYLE_BLOCK_HOLLOW: info->cursor_style = 3; break;
    default: info->cursor_style = 0; break;
  }

  GhosttyRenderStateColors colors = GHOSTTY_INIT_SIZED(GhosttyRenderStateColors);
  ghostty_render_state_get(t->rs, GHOSTTY_RENDER_STATE_DATA_COLORS, &colors);
  const uint32_t dfg = pack(colors.foreground), dbg = pack(colors.background);
  info->default_fg = dfg;
  info->default_bg = dbg;
  info->cursor_color = colors.cursor_has_value ? pack(colors.cursor) : dfg;
  if (dirty == GHOSTTY_RENDER_STATE_DIRTY_FALSE) return 0;

  ghostty_render_state_get(t->rs, GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR, &t->it);
  uint16_t y = 0;
  int count = 0;
  while (ghostty_render_state_row_iterator_next_dirty(t->it, &y)) {
    if (y >= rows) continue;
    GhosttyRenderStateRowSelection sel = GHOSTTY_INIT_SIZED(GhosttyRenderStateRowSelection);
    bool has_sel = ghostty_render_state_row_get(t->it, GHOSTTY_RENDER_STATE_ROW_DATA_SELECTION, &sel) == GHOSTTY_SUCCESS;
    ghostty_render_state_row_get(t->it, GHOSTTY_RENDER_STATE_ROW_DATA_CELLS, &t->cells);
    RvtCell* row = out + (size_t)y * cols;
    uint16_t x = 0;
    while (x < cols && ghostty_render_state_row_cells_next(t->cells)) {
      RvtCell* c = &row[x];
      GhosttyCell raw = 0;
      ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_RAW, &raw);
      uint32_t cp = 0;
      ghostty_cell_get(raw, GHOSTTY_CELL_DATA_CODEPOINT, &cp);
      GhosttyCellWide wide = GHOSTTY_CELL_WIDE_NARROW;
      ghostty_cell_get(raw, GHOSTTY_CELL_DATA_WIDE, &wide);
      bool styled = false;
      ghostty_cell_get(raw, GHOSTTY_CELL_DATA_HAS_STYLING, &styled);
      GhosttyCellContentTag tag = GHOSTTY_CELL_CONTENT_CODEPOINT;
      ghostty_cell_get(raw, GHOSTTY_CELL_DATA_CONTENT_TAG, &tag);
      uint32_t glen = cp ? 1 : 0;
      if (tag == GHOSTTY_CELL_CONTENT_CODEPOINT_GRAPHEME)
        ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_LEN, &glen);

      uint32_t fg = dfg, bg = dbg;
      uint16_t flags = 0;
      bool bg_default = true;
      if (styled) {
        GhosttyStyle st = GHOSTTY_INIT_SIZED(GhosttyStyle);
        ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_STYLE, &st);
        fg = resolve(st.fg_color, &colors, dfg);
        bg_default = st.bg_color.tag == GHOSTTY_STYLE_COLOR_NONE;
        bg = resolve(st.bg_color, &colors, dbg);
        if (st.bold) flags |= RVT_BOLD;
        if (st.italic) flags |= RVT_ITALIC;
        if (st.underline) flags |= RVT_UNDERLINE;
        if (st.strikethrough) flags |= RVT_STRIKE;
        if (st.faint) flags |= RVT_FAINT;
        if (st.invisible) flags |= RVT_INVISIBLE;
        if (st.inverse) { uint32_t s = fg; fg = bg; bg = s; bg_default = false; }
      } else if (tag == GHOSTTY_CELL_CONTENT_BG_COLOR_PALETTE || tag == GHOSTTY_CELL_CONTENT_BG_COLOR_RGB) {
        GhosttyColorRgb rgb;
        if (ghostty_render_state_row_cells_get(t->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_BG_COLOR, &rgb) == GHOSTTY_SUCCESS) {
          bg = pack(rgb);
          bg_default = false;
        }
      }
      if (bg_default) flags |= RVT_DEFAULT_BG;
      if (has_sel && x >= sel.start_x && x <= sel.end_x) flags |= RVT_SELECTED;
      c->cp = cp; c->fg = fg; c->bg = bg; c->flags = flags; c->wide = (uint8_t)wide;
      c->glen = (uint8_t)(glen > 255 ? 255 : glen);
      x++;
    }
    for (; x < cols; x++) {
      RvtCell* c = &row[x];
      memset(c, 0, sizeof *c);
      c->fg = dfg; c->bg = dbg; c->flags = RVT_DEFAULT_BG;
      if (has_sel && x >= sel.start_x && x <= sel.end_x) c->flags |= RVT_SELECTED;
    }
    apply_spans(t, row, y, cols);
    dirty_rows[y] = 1;
    count++;
  }
  info->dirty_rows = (uint16_t)count;
  ghostty_render_state_clean(t->rs);
  return count;
}

// Reads a multi-codepoint grapheme for one viewport cell (emoji ZWJ sequences, combining
// marks). Returns the number of codepoints written.
RVT_API int rvt_cell_graphemes(RvtTerm* t, uint16_t x, uint16_t y, uint32_t* out, int cap) {
  GhosttyGridRef ref = GHOSTTY_INIT_SIZED(GhosttyGridRef);
  GhosttyPoint pt = { .tag = GHOSTTY_POINT_TAG_VIEWPORT, .value = { .coordinate = { .x = x, .y = y } } };
  int n = 0;
  lock(t);
  if (ghostty_terminal_grid_ref(t->term, pt, &ref) == GHOSTTY_SUCCESS) {
    size_t len = 0;
    if (ghostty_grid_ref_graphemes(&ref, out, (size_t)cap, &len) == GHOSTTY_SUCCESS)
      n = (int)len;
  }
  unlock(t);
  return n;
}

// OSC 8 hyperlink URI of one viewport cell. Returns its length in bytes: 0 when the cell has
// no hyperlink, -1 when the URI does not fit.
RVT_API int rvt_cell_hyperlink(RvtTerm* t, uint16_t x, uint16_t y, uint8_t* out, int cap) {
  GhosttyGridRef ref = GHOSTTY_INIT_SIZED(GhosttyGridRef);
  GhosttyPoint pt = { .tag = GHOSTTY_POINT_TAG_VIEWPORT, .value = { .coordinate = { .x = x, .y = y } } };
  int n = 0;
  lock(t);
  if (ghostty_terminal_grid_ref(t->term, pt, &ref) == GHOSTTY_SUCCESS) {
    size_t len = 0;
    GhosttyResult r = ghostty_grid_ref_hyperlink_uri(&ref, out, (size_t)cap, &len);
    n = r == GHOSTTY_SUCCESS ? (int)len : (len > 0 ? -1 : 0);
  }
  unlock(t);
  return n;
}

// ---- viewport -----------------------------------------------------------------------------

RVT_API void rvt_scroll(RvtTerm* t, int mode, intptr_t value) {
  GhosttyTerminalScrollViewport s;
  memset(&s, 0, sizeof s);
  switch (mode) {
    case 0: s.tag = GHOSTTY_SCROLL_VIEWPORT_TOP; break;
    case 1: s.tag = GHOSTTY_SCROLL_VIEWPORT_BOTTOM; break;
    case 2: s.tag = GHOSTTY_SCROLL_VIEWPORT_DELTA; s.value.delta = value; break;
    default: s.tag = GHOSTTY_SCROLL_VIEWPORT_ROW; s.value.row = (size_t)value; break;
  }
  lock(t);
  ghostty_terminal_scroll_viewport(t->term, s);
  unlock(t);
}

RVT_API void rvt_reset(RvtTerm* t) {
  lock(t);
  ghostty_terminal_reset(t->term);
  unlock(t);
}

// ---- keyboard -----------------------------------------------------------------------------

static GhosttyKey key_from_vk(int vk) {
  if (vk >= 'A' && vk <= 'Z') return (GhosttyKey)(GHOSTTY_KEY_A + (vk - 'A'));
  if (vk >= '0' && vk <= '9') return (GhosttyKey)(GHOSTTY_KEY_DIGIT_0 + (vk - '0'));
  if (vk >= 0x60 && vk <= 0x69) return (GhosttyKey)(GHOSTTY_KEY_NUMPAD_0 + (vk - 0x60));
  if (vk >= 0x70 && vk <= 0x7B) return (GhosttyKey)(GHOSTTY_KEY_F1 + (vk - 0x70));
  switch (vk) {
    case 0x08: return GHOSTTY_KEY_BACKSPACE;
    case 0x09: return GHOSTTY_KEY_TAB;
    case 0x0D: return GHOSTTY_KEY_ENTER;
    case 0x1B: return GHOSTTY_KEY_ESCAPE;
    case 0x20: return GHOSTTY_KEY_SPACE;
    case 0x21: return GHOSTTY_KEY_PAGE_UP;
    case 0x22: return GHOSTTY_KEY_PAGE_DOWN;
    case 0x23: return GHOSTTY_KEY_END;
    case 0x24: return GHOSTTY_KEY_HOME;
    case 0x25: return GHOSTTY_KEY_ARROW_LEFT;
    case 0x26: return GHOSTTY_KEY_ARROW_UP;
    case 0x27: return GHOSTTY_KEY_ARROW_RIGHT;
    case 0x28: return GHOSTTY_KEY_ARROW_DOWN;
    case 0x2D: return GHOSTTY_KEY_INSERT;
    case 0x2E: return GHOSTTY_KEY_DELETE;
    case 0x5D: return GHOSTTY_KEY_CONTEXT_MENU;
    case 0x6A: return GHOSTTY_KEY_NUMPAD_MULTIPLY;
    case 0x6B: return GHOSTTY_KEY_NUMPAD_ADD;
    case 0x6C: return GHOSTTY_KEY_NUMPAD_SEPARATOR;
    case 0x6D: return GHOSTTY_KEY_NUMPAD_SUBTRACT;
    case 0x6E: return GHOSTTY_KEY_NUMPAD_DECIMAL;
    case 0x6F: return GHOSTTY_KEY_NUMPAD_DIVIDE;
    case 0xBA: return GHOSTTY_KEY_SEMICOLON;
    case 0xBB: return GHOSTTY_KEY_EQUAL;
    case 0xBC: return GHOSTTY_KEY_COMMA;
    case 0xBD: return GHOSTTY_KEY_MINUS;
    case 0xBE: return GHOSTTY_KEY_PERIOD;
    case 0xBF: return GHOSTTY_KEY_SLASH;
    case 0xC0: return GHOSTTY_KEY_BACKQUOTE;
    case 0xDB: return GHOSTTY_KEY_BRACKET_LEFT;
    case 0xDC: return GHOSTTY_KEY_BACKSLASH;
    case 0xDD: return GHOSTTY_KEY_BRACKET_RIGHT;
    case 0xDE: return GHOSTTY_KEY_QUOTE;
    case 0xE2: return GHOSTTY_KEY_INTL_BACKSLASH;
    default: return GHOSTTY_KEY_UNIDENTIFIED;
  }
}

// mods: bit 0 shift, 1 ctrl, 2 alt, 3 super (same as GHOSTTY_MODS_*). action: 0 release,
// 1 press, 2 repeat. utf8 is the text the key produces without ctrl/alt (may be empty).
// Returns bytes written to out (0 when the key produces nothing), or -1 for an unmapped key.
RVT_API int rvt_encode_key(RvtTerm* t, int vk, int mods, int action, const char* utf8, size_t utf8_len,
                           uint32_t unshifted, char* out, size_t cap) {
  GhosttyKey key = key_from_vk(vk);
  if (key == GHOSTTY_KEY_UNIDENTIFIED) return -1;
  size_t written = 0;
  lock(t);
  ghostty_key_encoder_setopt_from_terminal(t->keys, t->term);
  ghostty_key_event_set_action(t->key, (GhosttyKeyAction)action);
  ghostty_key_event_set_key(t->key, key);
  ghostty_key_event_set_mods(t->key, (GhosttyMods)mods);
  ghostty_key_event_set_consumed_mods(t->key, 0);
  ghostty_key_event_set_utf8(t->key, utf8_len ? utf8 : NULL, utf8_len);
  ghostty_key_event_set_unshifted_codepoint(t->key, unshifted);
  GhosttyResult r = ghostty_key_encoder_encode(t->keys, t->key, out, cap, &written);
  unlock(t);
  return r == GHOSTTY_SUCCESS ? (int)written : 0;
}

// Kitty keyboard flags the running program enabled (0 = legacy encoding).
RVT_API int rvt_kitty_flags(RvtTerm* t) {
  uint8_t flags = 0;
  lock(t);
  ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_KITTY_KEYBOARD_FLAGS, &flags);
  unlock(t);
  return flags;
}

// Focus in/out report (CSI I / CSI O) when the program enabled mode 1004; 0 otherwise.
RVT_API int rvt_focus(RvtTerm* t, int focused, char* out, size_t cap) {
  GhosttyTerminalModeConfig mode = { GHOSTTY_MODE_FOCUS_EVENT, false };
  size_t written = 0;
  lock(t);
  bool on = ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_MODE, &mode) == GHOSTTY_SUCCESS && mode.value;
  unlock(t);
  if (on && ghostty_focus_encode(focused ? GHOSTTY_FOCUS_GAINED : GHOSTTY_FOCUS_LOST, out, cap, &written) != GHOSTTY_SUCCESS)
    written = 0;
  return (int)written;
}

// ---- mouse --------------------------------------------------------------------------------

RVT_API int rvt_mouse_tracking(RvtTerm* t) {
  bool tracking = false;
  lock(t);
  ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_MOUSE_TRACKING, &tracking);
  unlock(t);
  return tracking ? 1 : 0;
}

// action: 0 press, 1 release, 2 motion. button: 0 none, 1 left, 2 right, 3 middle, 4/5 wheel.
// x/y are surface pixels. Returns bytes for the program (0 when it does not track this event).
RVT_API int rvt_encode_mouse(RvtTerm* t, int action, int button, int mods, float x, float y,
                             int any_button_pressed, char* out, size_t cap) {
  size_t written = 0;
  lock(t);
  ghostty_mouse_encoder_setopt_from_terminal(t->mouse, t->term);
  GhosttyMouseEncoderSize size = GHOSTTY_INIT_SIZED(GhosttyMouseEncoderSize);
  size.screen_width = t->screen_w; size.screen_height = t->screen_h;
  size.cell_width = t->cell_w; size.cell_height = t->cell_h;
  ghostty_mouse_encoder_setopt(t->mouse, GHOSTTY_MOUSE_ENCODER_OPT_SIZE, &size);
  bool pressed = any_button_pressed != 0;
  ghostty_mouse_encoder_setopt(t->mouse, GHOSTTY_MOUSE_ENCODER_OPT_ANY_BUTTON_PRESSED, &pressed);
  ghostty_mouse_event_set_action(t->mouse_event, (GhosttyMouseAction)action);
  if (button) ghostty_mouse_event_set_button(t->mouse_event, (GhosttyMouseButton)button);
  else ghostty_mouse_event_clear_button(t->mouse_event);
  ghostty_mouse_event_set_mods(t->mouse_event, (GhosttyMods)mods);
  GhosttyMousePosition pos = { x, y };
  ghostty_mouse_event_set_position(t->mouse_event, pos);
  GhosttyResult r = ghostty_mouse_encoder_encode(t->mouse, t->mouse_event, out, cap, &written);
  unlock(t);
  return r == GHOSTTY_SUCCESS ? (int)written : 0;
}

// ---- selection and clipboard --------------------------------------------------------------

// Selects from (x0, y0) to (x1, y1) inclusive, in viewport cells. Returns 0 on success.
RVT_API int rvt_select(RvtTerm* t, uint16_t x0, uint16_t y0, uint16_t x1, uint16_t y1) {
  GhosttyGridRef a = GHOSTTY_INIT_SIZED(GhosttyGridRef), b = GHOSTTY_INIT_SIZED(GhosttyGridRef);
  GhosttyPoint pa = { .tag = GHOSTTY_POINT_TAG_VIEWPORT, .value = { .coordinate = { .x = x0, .y = y0 } } };
  GhosttyPoint pb = { .tag = GHOSTTY_POINT_TAG_VIEWPORT, .value = { .coordinate = { .x = x1, .y = y1 } } };
  int rc = -1;
  lock(t);
  if (ghostty_terminal_grid_ref(t->term, pa, &a) == GHOSTTY_SUCCESS &&
      ghostty_terminal_grid_ref(t->term, pb, &b) == GHOSTTY_SUCCESS) {
    GhosttySelection sel = GHOSTTY_INIT_SIZED(GhosttySelection);
    sel.start = a;
    sel.end = b;
    sel.rectangle = false;
    rc = ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_SELECTION, &sel) == GHOSTTY_SUCCESS ? 0 : -1;
  }
  unlock(t);
  return rc;
}

// Selects everything selectable (scrollback included). Returns 0 on success.
RVT_API int rvt_select_all(RvtTerm* t) {
  int rc = -1;
  lock(t);
  GhosttySelection sel = GHOSTTY_INIT_SIZED(GhosttySelection);
  if (ghostty_terminal_select_all(t->term, &sel) == GHOSTTY_SUCCESS)
    rc = ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_SELECTION, &sel) == GHOSTTY_SUCCESS ? 0 : -1;
  unlock(t);
  return rc;
}

RVT_API void rvt_select_clear(RvtTerm* t) {
  lock(t);
  ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_SELECTION, NULL);
  unlock(t);
}

// Plain text of the current selection; free with rvt_free_buffer. Returns NULL when empty.
RVT_API uint8_t* rvt_selection_text(RvtTerm* t, size_t* out_len) {
  GhosttyTerminalSelectionFormatOptions opts = GHOSTTY_INIT_SIZED(GhosttyTerminalSelectionFormatOptions);
  opts.emit = GHOSTTY_FORMATTER_FORMAT_PLAIN;
  opts.trim = true;
  opts.unwrap = true;
  opts.selection = NULL;
  uint8_t* buf = NULL;
  *out_len = 0;
  lock(t);
  GhosttyResult r = ghostty_terminal_selection_format_alloc(t->term, NULL, opts, &buf, out_len);
  unlock(t);
  return r == GHOSTTY_SUCCESS ? buf : NULL;
}

RVT_API void rvt_free_buffer(uint8_t* buf, size_t len) {
  if (buf) ghostty_free(NULL, buf, len);
}

// The whole active screen (history included) as VT text: styles, wraps, cursor, modes and
// scrolling region, ready to replay into a fresh terminal of the same size. This is the
// rewind keyframe format the WebView player already understands. Free with rvt_free_buffer.
RVT_API uint8_t* rvt_format_vt(RvtTerm* t, size_t* out_len) {
  GhosttyFormatterTerminalOptions opts = GHOSTTY_INIT_SIZED(GhosttyFormatterTerminalOptions);
  opts.emit = GHOSTTY_FORMATTER_FORMAT_VT;
  opts.unwrap = true;
  opts.trim = true;
  opts.extra.size = sizeof opts.extra;
  opts.extra.modes = true;
  opts.extra.scrolling_region = true;
  opts.extra.tabstops = true;
  opts.extra.screen.size = sizeof opts.extra.screen;
  opts.extra.screen.cursor = true;
  opts.extra.screen.style = true;
  opts.extra.screen.hyperlink = true;
  opts.extra.screen.charsets = true;
  opts.selection = NULL;
  uint8_t* buf = NULL;
  *out_len = 0;
  lock(t);
  GhosttyFormatter f = NULL;
  if (ghostty_formatter_terminal_new(NULL, &f, t->term, opts) == GHOSTTY_SUCCESS) {
    if (ghostty_formatter_format_alloc(f, NULL, &buf, out_len) != GHOSTTY_SUCCESS) {
      buf = NULL;
      *out_len = 0;
    }
    ghostty_formatter_free(f);
  }
  unlock(t);
  return buf;
}

// ---- search ---------------------------------------------------------------------------------

// Sets (or with len 0 clears) the find needle. Matching is libghostty-vt's: byte-exact
// except ASCII letters, which compare case-insensitively.
RVT_API int rvt_search_set(RvtTerm* t, const uint8_t* needle, size_t len) {
  int rc = 0;
  lock(t);
  if (!t->search && len > 0 && ghostty_search_new(NULL, &t->search, t->term) != GHOSTTY_SUCCESS) rc = -1;
  if (t->search) {
    GhosttyString s = { needle, len };
    ghostty_search_set(t->search, GHOSTTY_SEARCH_OPT_NEEDLE, len ? &s : NULL);
    if (len) ghostty_search_run(t->search);
  }
  t->searching = len > 0 && t->search != NULL;
  unlock(t);
  return rc;
}

// direction: 1 toward older output (up), -1 toward newer. Scrolls the match into view.
RVT_API void rvt_search_step(RvtTerm* t, int direction) {
  lock(t);
  if (t->searching)
    ghostty_search_set(t->search, direction > 0 ? GHOSTTY_SEARCH_OPT_SELECT_NEXT : GHOSTTY_SEARCH_OPT_SELECT_PREV, NULL);
  unlock(t);
}

// Match count and 1-based position of the selected match (0 when none is selected).
RVT_API void rvt_search_status(RvtTerm* t, size_t* total, size_t* current) {
  *total = 0; *current = 0;
  lock(t);
  if (t->searching) {
    ghostty_search_get(t->search, GHOSTTY_SEARCH_DATA_TOTAL_MATCHES, total);
    size_t index = 0;
    if (ghostty_search_get(t->search, GHOSTTY_SEARCH_DATA_SELECTED_INDEX, &index) == GHOSTTY_SUCCESS)
      *current = index + 1;
  }
  unlock(t);
}

// ---- selection gestures -------------------------------------------------------------------

// kind: 0 press, 1 release, 2 drag, 3 autoscroll tick. (x, y) are surface pixels including
// the left padding; (vx, vy) the viewport cell under the pointer, already clamped to the
// grid. Click counting, word (double click) and line (triple click) selection, and
// granular drag extension are libghostty-vt's. Installs the resulting selection.
// Returns 1 when a selection was installed, 0 when the event produced none, -1 on error.
// *autoscroll receives 0 none, 1 up, 2 down; *clicks the click count; *dragged whether
// the gesture has dragged. flags: 1 rectangular (Alt+drag), 2 extend the current selection to
// this cell instead of starting a new one (Shift+click).
RVT_API int rvt_gesture(RvtTerm* t, int kind, double x, double y, uint16_t vx, uint16_t vy, uint64_t time_ns,
                        uint32_t padding_left, int flags, int* autoscroll, int* clicks, int* dragged) {
  static const GhosttySelectionGestureEventType types[4] = {
    GHOSTTY_SELECTION_GESTURE_EVENT_TYPE_PRESS, GHOSTTY_SELECTION_GESTURE_EVENT_TYPE_RELEASE,
    GHOSTTY_SELECTION_GESTURE_EVENT_TYPE_DRAG, GHOSTTY_SELECTION_GESTURE_EVENT_TYPE_AUTOSCROLL_TICK };
  if (kind < 0 || kind > 3) return -1;
  int rc = -1;
  lock(t);
  if (!t->gesture && ghostty_selection_gesture_new(NULL, &t->gesture) != GHOSTTY_SUCCESS) goto done;
  if (!t->gesture_events[kind] &&
      ghostty_selection_gesture_event_new(NULL, &t->gesture_events[kind], types[kind]) != GHOSTTY_SUCCESS) goto done;
  GhosttySelectionGestureEvent ev = t->gesture_events[kind];

  GhosttyGridRef ref = GHOSTTY_INIT_SIZED(GhosttyGridRef);
  GhosttyPoint pt = { .tag = GHOSTTY_POINT_TAG_VIEWPORT, .value = { .coordinate = { .x = vx, .y = vy } } };
  bool have_ref = ghostty_terminal_grid_ref(t->term, pt, &ref) == GHOSTTY_SUCCESS;
  GhosttySurfacePosition pos = { x, y };
  bool rectangle = (flags & 1) != 0;
  if (kind == 0 && (flags & 2) && have_ref) {
    GhosttySelection current = GHOSTTY_INIT_SIZED(GhosttySelection);
    if (ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_SELECTION, &current) == GHOSTTY_SUCCESS) {
      current.end = ref;
      rc = ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_SELECTION, &current) == GHOSTTY_SUCCESS ? 1 : -1;
      if (autoscroll) *autoscroll = 0;
      if (clicks) *clicks = 1;
      if (dragged) *dragged = 1; // a changed selection: copy-on-select copies it
      unlock(t);
      return rc;
    }
  }
  GhosttySelectionGestureGeometry geo = { t->cell_w ? t->screen_w / t->cell_w : 1, t->cell_w ? t->cell_w : 1,
                                          padding_left, t->screen_h ? t->screen_h : 1 };
  if (kind == 0) {
    uint64_t interval = 500000000ull; // 500 ms, the Windows default double-click time
    double distance = 4;
    if (!have_ref) goto done;
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REF, &ref);
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_POSITION, &pos);
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_TIME_NS, &time_ns);
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REPEAT_INTERVAL_NS, &interval);
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REPEAT_DISTANCE, &distance);
  } else if (kind == 1) {
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REF, have_ref ? &ref : NULL);
  } else if (kind == 2) {
    if (!have_ref) goto done;
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_REF, &ref);
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_POSITION, &pos);
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_GEOMETRY, &geo);
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_RECTANGLE, &rectangle);
  } else {
    GhosttyPointCoordinate vp = { vx, vy };
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_VIEWPORT, &vp);
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_POSITION, &pos);
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_GEOMETRY, &geo);
    ghostty_selection_gesture_event_set(ev, GHOSTTY_SELECTION_GESTURE_EVENT_OPT_RECTANGLE, &rectangle);
  }

  GhosttySelection sel = GHOSTTY_INIT_SIZED(GhosttySelection);
  GhosttyResult r = ghostty_selection_gesture_event(t->gesture, t->term, ev, &sel);
  if (r == GHOSTTY_SUCCESS) {
    rc = ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_SELECTION, &sel) == GHOSTTY_SUCCESS ? 1 : -1;
  } else if (r == GHOSTTY_NO_VALUE) {
    if (kind == 0) ghostty_terminal_set(t->term, GHOSTTY_TERMINAL_OPT_SELECTION, NULL); // a plain click clears
    rc = 0;
  }

  GhosttySelectionGestureAutoscroll as = GHOSTTY_SELECTION_GESTURE_AUTOSCROLL_NONE;
  uint8_t count = 0;
  bool drag = false;
  ghostty_selection_gesture_get(t->gesture, t->term, GHOSTTY_SELECTION_GESTURE_DATA_AUTOSCROLL, &as);
  ghostty_selection_gesture_get(t->gesture, t->term, GHOSTTY_SELECTION_GESTURE_DATA_CLICK_COUNT, &count);
  ghostty_selection_gesture_get(t->gesture, t->term, GHOSTTY_SELECTION_GESTURE_DATA_DRAGGED, &drag);
  *autoscroll = (int)as;
  *clicks = count;
  *dragged = drag ? 1 : 0;
done:
  unlock(t);
  return rc;
}

// ---- buffer access for the command tracker -------------------------------------------------

typedef struct {
  int32_t cursor_line;   // absolute
  int32_t cursor_x;
  int32_t viewport_top;  // absolute line at the top of the viewport
  int32_t total_lines;   // scrollback + active rows
  int32_t rows;
  int32_t cols;
  int32_t alternate;
} RvtBufferInfo;

RVT_API void rvt_buffer_info(RvtTerm* t, RvtBufferInfo* out) {
  memset(out, 0, sizeof *out);
  GhosttyTerminalScrollbar bar = { 0 };
  uint16_t cols = 0, rows = 0, x = 0;
  size_t total = 0;
  lock(t);
  out->cursor_line = cursor_line(t, &x);
  out->cursor_x = x;
  ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_SCROLLBAR, &bar);
  ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_COLS, &cols);
  ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_ROWS, &rows);
  ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_TOTAL_ROWS, &total);
  out->alternate = alternate_active(t) ? 1 : 0;
  unlock(t);
  out->viewport_top = (int32_t)bar.offset;
  out->total_lines = (int32_t)total;
  out->rows = rows;
  out->cols = cols;
}

// Text of one absolute line, trailing blanks trimmed (xterm's translateToString(true)).
// *wrapped: the line continues the one above (a soft wrap). Returns bytes written, or -1
// when the line does not exist.
RVT_API int rvt_line_text(RvtTerm* t, int32_t line, uint8_t* out, size_t cap, int* wrapped) {
  *wrapped = 0;
  if (line < 0) return -1;
  int rc = -1;
  lock(t);
  uint16_t cols = 0;
  ghostty_terminal_get(t->term, GHOSTTY_TERMINAL_DATA_COLS, &cols);
  GhosttyGridRef a = GHOSTTY_INIT_SIZED(GhosttyGridRef), b = GHOSTTY_INIT_SIZED(GhosttyGridRef);
  GhosttyPoint pa = { .tag = GHOSTTY_POINT_TAG_SCREEN, .value = { .coordinate = { .x = 0, .y = (uint32_t)line } } };
  GhosttyPoint pb = { .tag = GHOSTTY_POINT_TAG_SCREEN, .value = { .coordinate = { .x = (uint16_t)(cols ? cols - 1 : 0), .y = (uint32_t)line } } };
  if (cols && ghostty_terminal_grid_ref(t->term, pa, &a) == GHOSTTY_SUCCESS &&
      ghostty_terminal_grid_ref(t->term, pb, &b) == GHOSTTY_SUCCESS) {
    GhosttyRow row = 0;
    if (ghostty_grid_ref_row(&a, &row) == GHOSTTY_SUCCESS) {
      bool cont = false;
      ghostty_row_get(row, GHOSTTY_ROW_DATA_WRAP_CONTINUATION, &cont);
      *wrapped = cont ? 1 : 0;
    }
    GhosttySelection sel = GHOSTTY_INIT_SIZED(GhosttySelection);
    sel.start = a;
    sel.end = b;
    GhosttyTerminalSelectionFormatOptions opts = GHOSTTY_INIT_SIZED(GhosttyTerminalSelectionFormatOptions);
    opts.emit = GHOSTTY_FORMATTER_FORMAT_PLAIN;
    opts.trim = true;
    opts.unwrap = false;
    opts.selection = &sel;
    size_t written = 0;
    GhosttyResult r = ghostty_terminal_selection_format_buf(t->term, opts, out, cap, &written);
    if (r == GHOSTTY_SUCCESS) rc = (int)written;
    else if (r == GHOSTTY_NO_VALUE) rc = 0; // an empty line
    while (rc > 0 && (out[rc - 1] == '\n' || out[rc - 1] == '\r')) rc--;
  }
  unlock(t);
  return rc;
}

RVT_API uint32_t rvt_marker_new(RvtTerm* t, int32_t line) {
  lock(t);
  uint32_t id = alternate_active(t) ? 0 : marker_new_locked(t, line);
  unlock(t);
  return id;
}

// Current absolute line of a marker, or -1 once its line has left the scrollback.
RVT_API int32_t rvt_marker_line(RvtTerm* t, uint32_t id) {
  if (id == 0 || id > RVT_MAX_MARKERS) return -1;
  int32_t line = -1;
  lock(t);
  GhosttyTrackedGridRef ref = t->markers[id - 1];
  GhosttyPointCoordinate p;
  if (ref && ghostty_tracked_grid_ref_has_value(ref) &&
      ghostty_tracked_grid_ref_point(ref, GHOSTTY_POINT_TAG_SCREEN, &p) == GHOSTTY_SUCCESS)
    line = (int32_t)p.y;
  unlock(t);
  return line;
}

RVT_API void rvt_marker_free(RvtTerm* t, uint32_t id) {
  if (id == 0 || id > RVT_MAX_MARKERS) return;
  lock(t);
  if (t->markers[id - 1]) ghostty_tracked_grid_ref_free(t->markers[id - 1]);
  t->markers[id - 1] = NULL;
  unlock(t);
}

// Absolute lines of every find match (newest first, up to cap) for the ruler, and the
// selected match's line in *current (-1 when none). Returns the number written.
RVT_API int rvt_search_lines(RvtTerm* t, int32_t* out, int cap, int32_t* current) {
  *current = -1;
  if (cap <= 0) return 0;
  int n = 0;
  GhosttySelection* buf = (GhosttySelection*)calloc((size_t)cap, sizeof(GhosttySelection));
  if (!buf) return 0;
  lock(t);
  if (t->searching && t->search) {
    GhosttySelectionBuffer matches = { buf, (size_t)cap, 0 };
    GhosttyResult r = ghostty_search_get(t->search, GHOSTTY_SEARCH_DATA_MATCHES, &matches);
    // More matches than fit leaves the buffer unfilled: no ticks rather than wrong ones.
    size_t count = r == GHOSTTY_SUCCESS ? matches.len : 0;
    for (size_t i = 0; i < count && n < cap; i++) {
      GhosttyPointCoordinate p;
      if (ghostty_terminal_point_from_grid_ref(t->term, &buf[i].start, GHOSTTY_POINT_TAG_SCREEN, &p) == GHOSTTY_SUCCESS)
        out[n++] = (int32_t)p.y;
    }
    GhosttySelection selected = GHOSTTY_INIT_SIZED(GhosttySelection);
    GhosttyPointCoordinate p;
    if (ghostty_search_get(t->search, GHOSTTY_SEARCH_DATA_SELECTED_MATCH, &selected) == GHOSTTY_SUCCESS &&
        ghostty_terminal_point_from_grid_ref(t->term, &selected.start, GHOSTTY_POINT_TAG_SCREEN, &p) == GHOSTTY_SUCCESS)
      *current = (int32_t)p.y;
  }
  unlock(t);
  free(buf);
  return n;
}

typedef struct { const uint8_t* data; size_t len; } RvtPasteSource;

static bool paste_reader(void* user, GhosttyString mime, GhosttyWriter writer) {
  (void)mime;
  RvtPasteSource* src = (RvtPasteSource*)user;
  return writer.write(writer.userdata, src->data, src->len);
}

// Pastes UTF-8 text as user input: bracketed when the program enabled mode 2004, unsafe
// control bytes stripped. The encoded bytes arrive through RVT_EVENT_PTY.
RVT_API int rvt_paste(RvtTerm* t, const uint8_t* text, size_t len) {
  RvtPasteSource src = { text, len };
  GhosttyString mime = { (const uint8_t*)"text/plain;charset=utf-8", 24 };
  GhosttyPaste paste = GHOSTTY_INIT_SIZED(GhosttyPaste);
  paste.location = GHOSTTY_CLIPBOARD_LOCATION_STANDARD;
  paste.source = GHOSTTY_PASTE_SOURCE_TEXT;
  paste.mimes = &mime;
  paste.mimes_len = 1;
  paste.reader.read = paste_reader;
  paste.reader.userdata = &src;
  paste.allow_unsafe = true;
  bool written = false;
  lock(t);
  GhosttyResult r = ghostty_terminal_paste(t->term, &paste, &written);
  unlock(t);
  return r == GHOSTTY_SUCCESS ? (written ? 1 : 0) : r;
}
